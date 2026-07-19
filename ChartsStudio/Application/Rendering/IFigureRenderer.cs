using AIFlashcardMaker.ChartsStudio.Domain.Context;
using AIFlashcardMaker.ChartsStudio.Domain.Specs;

namespace AIFlashcardMaker.ChartsStudio.Application.Rendering;

/// <summary>One figure to draw, at one size.</summary>
public sealed class RenderRequest
{
    public required FigureSpec Spec { get; init; }

    /// <summary>The context supplying the aggregates. Never a dataset.</summary>
    public required AnalysisContext Context { get; init; }

    public required int WidthPixels { get; init; }
    public required int HeightPixels { get; init; }

    /// <summary>
    /// Scales fonts and line weights with the raster so a large render is the SAME figure
    /// enlarged, not the same figure with microscopic type. 1.0 = screen.
    /// </summary>
    public double ScaleFactor { get; init; } = 1.0;

    /// <summary>
    /// Identifies this render for caching: the spec's visual identity plus the output size.
    /// Two figures of the same variable in the same form at the same size are the same picture.
    /// </summary>
    public string CacheKey => $"{Spec.ToRenderKey()}|{WidthPixels}x{HeightPixels}@{ScaleFactor:F2}";
}

/// <summary>The outcome of a render — an image, or a stated reason there is none.</summary>
public sealed class RenderResult
{
    /// <summary>PNG bytes, or null on failure.</summary>
    public byte[]? PngBytes { get; init; }

    public bool Succeeded => PngBytes is { Length: > 0 };

    /// <summary>
    /// User-facing reason the figure could not be drawn. A renderer failure degrades to a card
    /// that explains itself; it never crashes the sheet and never shows an empty frame.
    /// </summary>
    public string FailureReason { get; init; } = "";

    public static RenderResult Success(byte[] png) => new() { PngBytes = png };

    public static RenderResult Failure(string reason) => new() { FailureReason = reason };
}

/// <summary>
/// Charts Studio Phase 2 — the rendering contract.
///
/// The Domain never names a charting library. Everything above this interface works in specs
/// and contexts, so replacing ScottPlot would mean writing one new implementation and changing
/// nothing else.
///
/// Implementations must be safe to call off the UI thread and must not touch WPF types.
/// </summary>
public interface IFigureRenderer
{
    /// <summary>
    /// Draws one figure. Must never throw for ordinary problems — a missing variable or an
    /// unknown chart type is a Failure result with a reason, not an exception.
    /// </summary>
    RenderResult Render(RenderRequest request, CancellationToken cancellationToken);
}
