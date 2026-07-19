using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AIFlashcardMaker.ChartsStudio.Application.Rendering;
using AIFlashcardMaker.ChartsStudio.Domain.ChartTypes;
using AIFlashcardMaker.ChartsStudio.Domain.Context;
using AIFlashcardMaker.ChartsStudio.Domain.Export;
using AIFlashcardMaker.ChartsStudio.Domain.Specs;
using AIFlashcardMaker.ChartsStudio.Domain.Themes;
using AIFlashcardMaker.ChartsStudio.Infrastructure.Export;
using AIFlashcardMaker.ChartsStudio.Infrastructure.Persistence;

namespace AIFlashcardMaker.ChartsStudio.Application.Export;

/// <summary>
/// Charts Studio Phase 5 — the export pipeline, end to end:
///
///     KeptFigure → style resolution → renderer → encoder → atomic write
///
/// Fully headless: no WPF, no dialogs, no dispatcher. The export dialog is a thin skin over
/// this class, and the QA harness drives it exactly as the dialog does.
///
/// GUARANTEES
///   • WYSIWYG — every render uses the same canonical logical canvas and the same resolver as
///     the editor preview; the profile contributes only physical size and DPI (see ExportCanvas).
///   • CONTINUES PAST FAILURES — one broken figure costs one file, never the batch.
///   • CANCELLABLE — between every render; a cancelled run reports what it did finish.
///   • MEMORY-STABLE — figures are processed one at a time and buffers dropped after writing;
///     a within-run memo avoids re-rendering identical payloads without holding a cache open.
///   • DETERMINISTIC FILES — encoders embed no timestamps; the manifest carries the date, the
///     figure files do not, so a re-export of unchanged figures is byte-identical.
/// </summary>
public sealed class ExportService
{
    private readonly IFigureRenderer _renderer;

    public ExportService(IFigureRenderer renderer)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
    }

    public async Task<ExportRunResult> ExportAsync(
        ExportPlan plan,
        AnalysisContext context,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!plan.IsValid)
            return new ExportRunResult
            {
                Items = new[] { new ExportItemResult
                {
                    FigureId = "", Label = "Export", Success = false,
                    Error = "Nothing to export: choose at least one figure, one format and a destination."
                }},
                WasCancelled = false
            };

        // The whole run happens off the calling thread; progress posts back through IProgress.
        return await Task.Run(() => Run(plan, context, progress, cancellationToken), CancellationToken.None)
                         .ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------------

    private ExportRunResult Run(
        ExportPlan plan,
        AnalysisContext context,
        IProgress<ExportProgress>? progress,
        CancellationToken ct)
    {
        var items = new List<ExportItemResult>();
        bool cancelled = false;

        string figuresDir = plan.AsPackage
            ? Path.Combine(plan.DestinationDirectory, "figures")
            : plan.DestinationDirectory;

        int total = plan.Figures.Count * plan.Formats.Count;
        int completed = 0;

        var namer = new ExportFileNamer();
        var captionEntries = new List<CaptionEntry>();
        var manifestFigures = new List<ExportManifestFigure>();

        // Within-run memo: two shelf entries that are the same picture (duplicates before
        // styling) render once. Dropped when the run ends — this is not a cache with a life.
        var rasterMemo = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        for (int i = 0; i < plan.Figures.Count; i++)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }

            var figure = plan.Figures[i];
            int index = i + 1;

            var style = FigureStyleResolver.Resolve(figure.Spec, figure.Patch);
            style = FigureStyleResolver.WithFontScale(style, plan.Profile.FontScale);

            string baseName = namer.NameFor(plan.NameTemplate, new ExportNameContext
            {
                Index = index,
                Title = style.Title,
                StudyName = plan.StudyName,
                ChartTypeName = ChartTypeRegistry.Find(figure.Spec.ChartTypeId)?.DisplayName ?? "Chart"
            });

            var files = new List<string>();

            foreach (var format in plan.Formats)
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }

                var def = ExportFormatCatalog.Get(format);
                string fileName = baseName + def.Extension;
                string path = Path.Combine(figuresDir, fileName);
                string label = $"{baseName}{def.Extension}";

                progress?.Report(new ExportProgress
                { Completed = completed, Total = total, CurrentLabel = label });

                try
                {
                    byte[]? encoded = RenderAndEncode(figure, context, style, plan.Profile, def, rasterMemo, ct, out string renderError);

                    if (encoded is null)
                    {
                        items.Add(new ExportItemResult
                        { FigureId = figure.Id, Label = label, Success = false, Error = renderError });
                    }
                    else
                    {
                        var write = ExportFileWriter.Write(path, encoded);
                        items.Add(new ExportItemResult
                        {
                            FigureId = figure.Id,
                            Label = label,
                            Success = write.Success,
                            FilePath = write.Success ? path : "",
                            Error = write.Error
                        });
                        if (write.Success) files.Add(fileName);
                    }
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    break;
                }
                catch (Exception ex)
                {
                    // A single figure×format failure is contained here; the batch continues.
                    items.Add(new ExportItemResult
                    {
                        FigureId = figure.Id, Label = label, Success = false,
                        Error = $"Export failed ({ex.GetType().Name})."
                    });
                }

                completed++;
                progress?.Report(new ExportProgress
                { Completed = completed, Total = total, CurrentLabel = label });
            }

            if (files.Count > 0)
            {
                captionEntries.Add(new CaptionEntry
                { Index = index, Title = style.Title, Caption = style.Caption, FileName = files[0] });

                manifestFigures.Add(new ExportManifestFigure
                {
                    Index = index,
                    FigureId = figure.Id,
                    Title = style.Title,
                    ChartTypeId = figure.Spec.ChartTypeId,
                    Files = files,
                    SpecRenderKey = figure.Spec.ToRenderKey(),
                    PatchKey = FigurePatch.KeyOf(figure.Patch)
                });
            }

            if (cancelled) break;
        }

        // Side documents are written even after a partial batch: captions for what DID export
        // are useful; captions for nothing are not written at all.
        string captionsPath = "", manifestPath = "";

        if (plan.IncludeCaptions && captionEntries.Count > 0 && !ct.IsCancellationRequested)
        {
            string capDir = plan.AsPackage
                ? Path.Combine(plan.DestinationDirectory, "captions")
                : plan.DestinationDirectory;

            var txt = ExportFileWriter.Write(
                Path.Combine(capDir, "captions.txt"),
                Encoding.UTF8.GetBytes(CaptionComposer.ComposeText(captionEntries)));
            var md = ExportFileWriter.Write(
                Path.Combine(capDir, "captions.md"),
                Encoding.UTF8.GetBytes(CaptionComposer.ComposeMarkdown(captionEntries)));

            if (txt.Success) captionsPath = Path.Combine(capDir, "captions.txt");
            if (!txt.Success || !md.Success)
                items.Add(new ExportItemResult
                { FigureId = "", Label = "captions", Success = false, Error = txt.Error + md.Error });
        }

        if (plan.IncludeManifest && manifestFigures.Count > 0 && !ct.IsCancellationRequested)
        {
            var manifest = new ExportManifest
            {
                ProjectId = plan.ProjectId,
                ProjectTitle = plan.StudyName,
                ExportedAtUtc = DateTime.UtcNow,
                AppVersion = AppVersion(),
                RendererVersion = _renderer.RendererVersion,
                ChartsStudioSchemaVersion = ChartsStudioProjectState.CurrentSchemaVersion,
                ProfileId = plan.Profile.Id,
                WidthInches = plan.Profile.WidthInches,
                HeightInches = plan.Profile.HeightInches,
                Dpi = plan.Profile.Dpi,
                Formats = plan.Formats.Select(f => f.ToString().ToUpperInvariant()).ToList(),
                FigureCount = manifestFigures.Count,
                Figures = manifestFigures
            };

            string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            manifestPath = Path.Combine(plan.DestinationDirectory, "manifest.json");
            var write = ExportFileWriter.Write(manifestPath, Encoding.UTF8.GetBytes(json));
            if (!write.Success)
            {
                items.Add(new ExportItemResult
                { FigureId = "", Label = "manifest.json", Success = false, Error = write.Error });
                manifestPath = "";
            }
        }

        return new ExportRunResult
        {
            Items = items,
            WasCancelled = cancelled,
            CaptionsPath = captionsPath,
            ManifestPath = manifestPath
        };
    }

    // ---------------------------------------------------------------------------------

    /// <summary>Renders exactly what the format needs and encodes it. Null = render failed.</summary>
    private byte[]? RenderAndEncode(
        KeptFigure figure,
        AnalysisContext context,
        ResolvedFigureStyle style,
        ExportProfile profile,
        ExportFormatDefinition def,
        Dictionary<string, byte[]> rasterMemo,
        CancellationToken ct,
        out string error)
    {
        error = "";

        switch (def.Need)
        {
            case RenderNeed.Raster:
            {
                var request = RasterRequest(figure, context, style, profile);
                string memoKey = request.CacheKey;
                if (!rasterMemo.TryGetValue(memoKey, out byte[]? png))
                {
                    var result = _renderer.Render(request, ct);
                    if (!result.Succeeded) { error = result.FailureReason; return null; }
                    png = result.PngBytes!;
                    rasterMemo[memoKey] = png;
                }

                return ExportEncoders.Encode(def.Format, new EncodePayload
                { PngBytes = png, Profile = profile });
            }

            case RenderNeed.Vector:
            {
                int logicalH = profile.LogicalHeightForAspect;
                var request = new RenderRequest
                {
                    Spec = figure.Spec,
                    Context = context,
                    Style = style,
                    WidthPixels = ExportCanvas.LogicalWidth,
                    HeightPixels = logicalH,
                    ScaleFactor = 1.0
                };

                var result = _renderer.RenderSvg(request, ct);
                if (!result.Succeeded) { error = result.FailureReason; return null; }

                return ExportEncoders.Encode(def.Format, new EncodePayload
                {
                    SvgXml = result.SvgXml,
                    Profile = profile,
                    LogicalWidth = ExportCanvas.LogicalWidth,
                    LogicalHeight = logicalH
                });
            }

            case RenderNeed.Pixels:
            {
                var request = RasterRequest(figure, context, style, profile);
                var result = _renderer.RenderPixels(request, ct);
                if (!result.Succeeded) { error = result.FailureReason; return null; }

                return ExportEncoders.Encode(def.Format, new EncodePayload
                {
                    Rgb24 = result.Rgb24,
                    PixelWidth = result.Width,
                    PixelHeight = result.Height,
                    Profile = profile
                });
            }

            default:
                error = $"Unsupported render need {def.Need}.";
                return null;
        }
    }

    private static RenderRequest RasterRequest(
        KeptFigure figure, AnalysisContext context, ResolvedFigureStyle style, ExportProfile profile) => new()
    {
        Spec = figure.Spec,
        Context = context,
        Style = style,
        WidthPixels = profile.PixelWidth,
        HeightPixels = profile.PixelHeight,
        ScaleFactor = profile.ScaleFactor,
        PaddingPixels = profile.PaddingPixels,
        TransparentBackground = profile.TransparentBackground
    };

    private static string AppVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }
}
