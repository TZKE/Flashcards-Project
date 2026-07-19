namespace AIFlashcardMaker.ChartsStudio.Domain.Export;

/// <summary>Output formats Charts Studio can write today.</summary>
public enum ExportFormat
{
    Png,
    Svg,
    Pdf
}

/// <summary>
/// What a format needs from the renderer. The export service renders each need at most once
/// per figure and feeds every encoder that wants it — three formats never mean three renders
/// of the same payload kind.
/// </summary>
public enum RenderNeed
{
    /// <summary>Encoded raster (PNG bytes) at full export resolution.</summary>
    Raster,

    /// <summary>Vector XML at logical size; the encoder applies physical dimensions.</summary>
    Vector,

    /// <summary>Raw RGB24 pixel rows at full export resolution, for embedding.</summary>
    Pixels
}

/// <summary>
/// Charts Studio Phase 5 — the format catalogue: THE extension seam for output formats.
///
/// Adding TIFF, EPS, EMF, clipboard or document formats later means adding one definition here
/// plus its encoder — the pipeline, the service, the dialog and the tests do not change shape.
/// The list is code, not configuration: a format either ships working and tested or it does
/// not appear.
/// </summary>
public sealed class ExportFormatDefinition
{
    public required ExportFormat Format { get; init; }
    public required string Extension { get; init; }
    public required string DisplayName { get; init; }
    public required RenderNeed Need { get; init; }

    /// <summary>One honest line about what the file contains — shown in the dialog, because a
    /// researcher choosing PDF deserves to know it embeds a 600 DPI raster, not vector art.</summary>
    public required string Description { get; init; }
}

public static class ExportFormatCatalog
{
    private static readonly ExportFormatDefinition[] All =
    {
        new()
        {
            Format = ExportFormat.Png,
            Extension = ".png",
            DisplayName = "PNG",
            Need = RenderNeed.Raster,
            Description = "Raster image at the profile's DPI. Universal; ideal for submission systems that ask for high-resolution images."
        },
        new()
        {
            Format = ExportFormat.Svg,
            Extension = ".svg",
            DisplayName = "SVG",
            Need = RenderNeed.Vector,
            Description = "True vector graphics — infinitely scalable, editable in Illustrator or Inkscape."
        },
        new()
        {
            // ScottPlot 5 has no vector PDF surface. Rather than adding a PDF library to a
            // published, SHA-verified installer, the PDF embeds the figure as a raster at the
            // profile's full DPI inside a correctly sized page. 600 DPI raster is accepted by
            // journals; the description states the truth so nobody mistakes it for vector.
            Format = ExportFormat.Pdf,
            Extension = ".pdf",
            DisplayName = "PDF",
            Need = RenderNeed.Pixels,
            Description = "Single-page PDF at the exact physical size, embedding the figure at full DPI (high-resolution raster, not vector)."
        }
    };

    public static IReadOnlyList<ExportFormatDefinition> Formats => All;

    public static ExportFormatDefinition Get(ExportFormat format) =>
        All.First(f => f.Format == format);
}
