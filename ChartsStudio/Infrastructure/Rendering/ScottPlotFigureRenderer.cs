using AIFlashcardMaker.ChartsStudio.Application.Rendering;
using AIFlashcardMaker.ChartsStudio.Domain.ChartTypes;
using AIFlashcardMaker.ChartsStudio.Domain.Context;
using AIFlashcardMaker.ChartsStudio.Domain.Themes;

namespace AIFlashcardMaker.ChartsStudio.Infrastructure.Rendering;

/// <summary>
/// Charts Studio — the ScottPlot 5 renderer.
///
/// THE ONLY FILE IN THE MODULE THAT REFERENCES A CHARTING LIBRARY. Everything above works in
/// specs, contexts and resolved styles, so swapping engines means writing one new
/// implementation of IFigureRenderer and changing nothing else.
///
/// PHASE 3: rendering is now STYLE-DRIVEN. Every visual decision arrives pre-resolved and
/// pre-clamped in a ResolvedFigureStyle (spec + user patch + theme, merged by
/// FigureStyleResolver). This file translates those decisions into ScottPlot calls and adds
/// none of its own — if a colour or size is wrong, the bug is in the resolver where it is
/// unit-testable, not scattered through drawing code.
///
/// WYSIWYG IS THE CONTRACT. Thumbnail, editor preview and (later) export all pass through this
/// one method; print-resolution output uses ScaleFactor so a large raster is the same figure
/// enlarged, not the same figure with microscopic type.
///
/// RENDERS FROM AGGREGATES ONLY. Every number drawn was computed by Research Lab and projected
/// into the context. This renderer never sees a dataset and never calculates a statistic.
/// </summary>
public sealed class ScottPlotFigureRenderer : IFigureRenderer
{
    /// <summary>Engine identity for manifests and cache keys. Read from the assembly so it can
    /// never drift from the package actually shipped.</summary>
    public string RendererVersion { get; } =
        "ScottPlot " + (typeof(ScottPlot.Plot).Assembly.GetName().Version?.ToString(3) ?? "5");

    public RenderResult Render(RenderRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var (plot, failure) = BuildPlot(request, cancellationToken);
            if (plot is null) return RenderResult.Failure(failure);

            cancellationToken.ThrowIfCancellationRequested();

            using var surface = ComposeSurface(plot, request);
            byte[] png = EncodePngFast(surface);

            return png.Length > 0
                ? RenderResult.Success(png)
                : RenderResult.Failure("The figure produced no image.");
        }
        catch (OperationCanceledException)
        {
            throw;   // cancellation is the queue's business, not a figure failure
        }
        catch (Exception ex)
        {
            // A renderer fault degrades to a card that explains itself. It never takes down
            // the surface, and it never shows a blank frame pretending to be a figure.
            return RenderResult.Failure($"This figure could not be drawn ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// Phase 5 — SVG at the request's logical size. Vector output ignores ScaleFactor and
    /// padding by design: physical size and margins are applied by the encoder through the
    /// SVG's own coordinate system, losslessly.
    /// </summary>
    public VectorRenderResult RenderSvg(RenderRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var (plot, failure) = BuildPlot(request, cancellationToken);
            if (plot is null) return VectorRenderResult.Failure(failure);

            cancellationToken.ThrowIfCancellationRequested();

            string svg = plot.GetSvgXml(request.WidthPixels, request.HeightPixels);
            return string.IsNullOrEmpty(svg)
                ? VectorRenderResult.Failure("The figure produced no vector output.")
                : VectorRenderResult.Success(svg);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return VectorRenderResult.Failure($"This figure could not be drawn ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// Phase 5 — raw RGB24 rows for container embedding (PDF). Always flattened opaque: a PDF
    /// page has no transparency to inherit, so alpha is composited against the style's
    /// background here rather than surprising the user downstream.
    /// </summary>
    public PixelRenderResult RenderPixels(RenderRequest request, CancellationToken cancellationToken)
    {
        try
        {
            // Force opacity for the composition, whatever the request said.
            var opaque = new RenderRequest
            {
                Spec = request.Spec,
                Context = request.Context,
                Style = request.Style,
                WidthPixels = request.WidthPixels,
                HeightPixels = request.HeightPixels,
                ScaleFactor = request.ScaleFactor,
                PaddingPixels = request.PaddingPixels,
                TransparentBackground = false
            };

            var (plot, failure) = BuildPlot(opaque, cancellationToken);
            if (plot is null) return PixelRenderResult.Failure(failure);

            cancellationToken.ThrowIfCancellationRequested();

            using var surface = ComposeSurface(plot, opaque);
            using var image = surface.Snapshot();
            using var bitmap = SkiaSharp.SKBitmap.FromImage(image);

            int w = bitmap.Width, h = bitmap.Height;
            var rgb = new byte[w * h * 3];
            int i = 0;
            for (int y = 0; y < h; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int x = 0; x < w; x++)
                {
                    var c = bitmap.GetPixel(x, y);
                    rgb[i++] = c.Red;
                    rgb[i++] = c.Green;
                    rgb[i++] = c.Blue;
                }
            }

            return PixelRenderResult.Success(rgb, w, h);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return PixelRenderResult.Failure($"This figure could not be drawn ({ex.GetType().Name}).");
        }
    }

    // ---------------------------------------------------------------------------------
    // Shared build + composition
    // ---------------------------------------------------------------------------------

    /// <summary>One construction path for every output surface — raster, vector and pixels all
    /// see the same plot, which is the WYSIWYG guarantee at the code level.</summary>
    private static (ScottPlot.Plot? plot, string failure) BuildPlot(
        RenderRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var variable = request.Context.Variables.FirstOrDefault(v =>
            string.Equals(v.Id, request.Spec.PrimaryVariableId, StringComparison.Ordinal));

        if (variable is null)
            return (null, "The variable this figure was built from is no longer in the project.");

        // Null style = recommendation defaults. Resolving here (rather than requiring every
        // call site to) keeps pre-editor callers byte-identical to Phase 2.
        var style = request.Style ?? FigureStyleResolver.Resolve(request.Spec, null);

        var plot = new ScottPlot.Plot();
        ApplyChrome(plot, style, request.TransparentBackground);

        bool drawn = request.Spec.ChartTypeId switch
        {
            ChartTypeRegistry.BoxPlotId => DrawBoxPlot(plot, variable, style),
            ChartTypeRegistry.BarChartId => DrawBarChart(plot, variable, style),
            ChartTypeRegistry.MeanSdId => DrawMeanSd(plot, variable, style),
            _ => false
        };

        if (!drawn)
        {
            return ChartTypeRegistry.Find(request.Spec.ChartTypeId) is null
                ? (null, $"Unknown chart type '{request.Spec.ChartTypeId}'.")
                : (null, "The aggregates this figure needs are not available.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        ApplyText(plot, style);
        ApplyLegend(plot, variable, style);

        return (plot, "");
    }

    /// <summary>
    /// Draws the plot onto a padded (and possibly transparent) canvas. The figure itself is
    /// laid out at the INNER size — padding is breathing room around the approved layout, not
    /// a squeeze of it.
    ///
    /// ORDER MATTERS, learned from a raw-pixel probe: plot.Render(canvas, rect) begins by
    /// clearing the ENTIRE canvas transparent, not just its rect — so any background painted
    /// before it is silently erased (and the loss is invisible in image viewers, which
    /// composite transparency onto white). The padding band is therefore painted AFTER the
    /// plot renders, into the border the plot's clear left transparent.
    /// </summary>
    private static SkiaSharp.SKSurface ComposeSurface(ScottPlot.Plot plot, RenderRequest request)
    {
        int pad = Math.Max(0, request.PaddingPixels);
        int w = request.WidthPixels, h = request.HeightPixels;
        int innerW = Math.Max(16, w - 2 * pad);
        int innerH = Math.Max(16, h - 2 * pad);

        plot.ScaleFactor = (float)request.ScaleFactor;

        if (request.TransparentBackground)
        {
            // The figure interior itself must be see-through, not just the border.
            plot.FigureBackground.Color = ScottPlot.Colors.Transparent;
            plot.DataBackground.Color = ScottPlot.Colors.Transparent;
        }

        var info = new SkiaSharp.SKImageInfo(w, h,
            SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);

        var surface = SkiaSharp.SKSurface.Create(info);
        var canvas = surface.Canvas;

        plot.Render(canvas, new ScottPlot.PixelRect(pad, pad + innerW, pad + innerH, pad));

        if (pad > 0 && !request.TransparentBackground)
        {
            var style = request.Style ?? FigureStyleResolver.Resolve(request.Spec, null);
            using var paint = new SkiaSharp.SKPaint
            { Color = SkiaSharp.SKColor.Parse(style.BackgroundHex), IsAntialias = false };

            canvas.DrawRect(0, 0, w, pad, paint);                       // top band
            canvas.DrawRect(0, h - pad, w, pad, paint);                 // bottom band
            canvas.DrawRect(0, pad, pad, h - 2 * pad, paint);           // left band
            canvas.DrawRect(w - pad, pad, pad, h - 2 * pad, paint);     // right band
        }

        return surface;
    }

    /// <summary>
    /// PNG encoding tuned for figures: Sub filter, zlib level 3. Skia's default settings
    /// spent 136 ms of a 147 ms render+encode on compression alone (measured); Sub/3 encodes
    /// the same pixels losslessly in ~30 ms at ~3× the size — the right trade for interactive
    /// preview and comfortably inside the 150 ms export budget. Fixed options keep output
    /// deterministic.
    /// </summary>
    private static byte[] EncodePngFast(SkiaSharp.SKSurface surface)
    {
        using var image = surface.Snapshot();
        using var bitmap = SkiaSharp.SKBitmap.FromImage(image);
        using var pixmap = bitmap.PeekPixels();

        var options = new SkiaSharp.SKPngEncoderOptions(SkiaSharp.SKPngEncoderFilterFlags.Sub, 3);
        using var data = pixmap.Encode(options);
        return data?.ToArray() ?? Array.Empty<byte>();
    }

    // ---------------------------------------------------------------------------------
    // Chrome — backgrounds, axes, grid, fonts. All values arrive resolved and clamped.
    // ---------------------------------------------------------------------------------

    private static void ApplyChrome(ScottPlot.Plot plot, ResolvedFigureStyle style, bool transparent = false)
    {
        var background = Hex(style.BackgroundHex);
        var axisColor = Hex(style.AxisHex);

        plot.FigureBackground.Color = background;
        plot.DataBackground.Color = background;
        plot.Axes.Color(axisColor);

        if (style.ShowGrid)
        {
            plot.Grid.MajorLineColor = Hex(style.GridHex);
        }
        else
        {
            plot.HideGrid();
        }

        foreach (var axis in new ScottPlot.AxisPanels.AxisBase[]
                 { (ScottPlot.AxisPanels.AxisBase)plot.Axes.Left, (ScottPlot.AxisPanels.AxisBase)plot.Axes.Bottom })
        {
            axis.LabelFontName = style.FontFamily;
            axis.LabelFontSize = (float)style.AxisFontSize;
            axis.TickLabelStyle.FontName = style.FontFamily;
            axis.TickLabelStyle.FontSize = (float)style.TickFontSize;
        }

        // Hiding an axis hides the panel — ticks, tick labels and title together. The data
        // area keeps its frame, which is what a "clean" figure looks like in print.
        if (!style.ShowXAxis) ((ScottPlot.AxisPanels.AxisBase)plot.Axes.Bottom).IsVisible = false;
        if (!style.ShowYAxis) ((ScottPlot.AxisPanels.AxisBase)plot.Axes.Left).IsVisible = false;
    }

    private static void ApplyText(ScottPlot.Plot plot, ResolvedFigureStyle style)
    {
        // ScottPlot has no subtitle concept; a second, unstyled line under the title is the
        // honest equivalent and survives export at any DPI.
        string title = string.IsNullOrWhiteSpace(style.Subtitle)
            ? style.Title
            : style.Title + "\n" + style.Subtitle;

        if (!string.IsNullOrWhiteSpace(title))
        {
            plot.Title(title, (float)style.TitleFontSize);
            plot.Axes.Title.Label.FontName = style.FontFamily;
        }

        if (!string.IsNullOrWhiteSpace(style.YLabel) && style.ShowYAxis)
            plot.YLabel(style.YLabel, (float)style.AxisFontSize);

        if (!string.IsNullOrWhiteSpace(style.XLabel) && style.ShowXAxis)
            plot.XLabel(style.XLabel, (float)style.AxisFontSize);
    }

    private static void ApplyLegend(ScottPlot.Plot plot, ContextVariable variable, ResolvedFigureStyle style)
    {
        if (style.ShowLegend)
        {
            var legend = plot.ShowLegend();
            legend.FontName = style.FontFamily;
            legend.FontSize = (float)style.TickFontSize;
        }
        else
        {
            // Plottables carry LegendText (so the legend is populated when wanted), and
            // ScottPlot auto-shows a legend once any plottable has text. Hide explicitly or
            // every figure grows a legend the user never asked for.
            plot.HideLegend();
        }
    }

    // ---------------------------------------------------------------------------------
    // Forms
    // ---------------------------------------------------------------------------------

    private static bool DrawBoxPlot(ScottPlot.Plot plot, ContextVariable v, ResolvedFigureStyle style)
    {
        if (!v.HasFiveNumberSummary) return false;

        var box = new ScottPlot.Box
        {
            Position = 1,
            BoxMin = v.Q1!.Value,
            BoxMiddle = v.Median!.Value,
            BoxMax = v.Q3!.Value,
            WhiskerMin = v.Min!.Value,
            WhiskerMax = v.Max!.Value,
            Width = 0.6
        };

        var boxes = plot.Add.Boxes(new[] { box });
        boxes.LegendText = v.DisplayName;

        // Style at the PLOTTABLE level, AFTER adding. Add.Boxes assigns the next palette
        // colour on add, silently clobbering anything set on the Box beforehand — caught
        // visually when a user's series colour changed the outline but not the fill.
        boxes.FillColor = Hex(style.SeriesFillHex).WithAlpha(style.Opacity);
        boxes.LineColor = Hex(style.SeriesLineHex);
        boxes.LineWidth = (float)style.LineWidth;

        plot.Axes.SetLimitsX(0.2, 1.8);
        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual();
        return true;
    }

    private static bool DrawBarChart(ScottPlot.Plot plot, ContextVariable v, ResolvedFigureStyle style)
    {
        if (!v.HasCategories) return false;

        var bars = new List<ScottPlot.Bar>();
        var ticks = new List<ScottPlot.Tick>();

        for (int i = 0; i < v.Categories.Count; i++)
        {
            var c = v.Categories[i];
            bars.Add(new ScottPlot.Bar { Position = i, Value = c.Count, Size = 0.7 });
            ticks.Add(new ScottPlot.Tick(i, c.DisplayLabel));
        }

        var barPlot = plot.Add.Bars(bars);
        barPlot.LegendText = v.DisplayName;
        barPlot.Horizontal = style.Horizontal;

        // Colour AFTER adding, for the same reason as the box plot: Add.Bars restyles bars
        // with a palette colour on add. These are the same Bar instances, so styling them now
        // is what actually reaches the render.
        for (int i = 0; i < bars.Count; i++)
        {
            string fillHex = style.CategoryPalette is { Length: > 0 }
                ? style.CategoryPalette[i % style.CategoryPalette.Length]
                : style.SeriesFillHex;

            bars[i].FillColor = Hex(fillHex).WithAlpha(style.Opacity);
            bars[i].LineColor = Hex(FigureStyleResolver.Darken(fillHex, 0.6));
            bars[i].LineWidth = (float)style.LineWidth;
        }

        if (style.Horizontal)
        {
            // Categories move to the Y axis; the value axis becomes X. The resolver already
            // guaranteed Horizontal only for bar charts, so no form re-check here.
            plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericManual(ticks.ToArray());
            ((ScottPlot.AxisPanels.AxisBase)plot.Axes.Left).MajorTickStyle.Length = 0;
            plot.Axes.SetLimitsX(0, bars.Max(b => b.Value) * 1.15);

            // Swap the axis titles to follow their content.
            if (!string.IsNullOrWhiteSpace(style.XLabel) && style.ShowYAxis)
                plot.YLabel(style.XLabel, (float)style.AxisFontSize);
            if (!string.IsNullOrWhiteSpace(style.YLabel) && style.ShowXAxis)
                plot.XLabel(style.YLabel, (float)style.AxisFontSize);
        }
        else
        {
            plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(ticks.ToArray());
            ((ScottPlot.AxisPanels.AxisBase)plot.Axes.Bottom).MajorTickStyle.Length = 0;
            plot.Axes.SetLimitsY(0, bars.Max(b => b.Value) * 1.15);
        }

        return true;
    }

    private static bool DrawMeanSd(ScottPlot.Plot plot, ContextVariable v, ResolvedFigureStyle style)
    {
        if (!v.HasMeanAndSd) return false;

        double mean = v.Mean!.Value, sd = v.StdDev!.Value;

        var marker = plot.Add.Marker(1, mean);
        marker.MarkerStyle.Shape = ScottPlot.MarkerShape.FilledCircle;
        marker.MarkerStyle.Size = (float)style.MarkerSize;
        marker.MarkerStyle.FillColor = Hex(style.SeriesFillHex).WithAlpha(style.Opacity);
        marker.LegendText = v.DisplayName;

        var errorBar = plot.Add.ErrorBar(
            xs: new[] { 1.0 },
            ys: new[] { mean },
            yErrors: new[] { sd });
        errorBar.Color = Hex(style.SeriesLineHex);
        errorBar.LineWidth = (float)style.LineWidth;

        plot.Axes.SetLimitsX(0.2, 1.8);
        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual();
        return true;
    }

    // ---------------------------------------------------------------------------------

    private static ScottPlot.Color Hex(string hex) => ScottPlot.Color.FromHex(hex);
}
