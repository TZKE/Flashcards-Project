using AIFlashcardMaker.ChartsStudio.Domain.Context;
using AIFlashcardMaker.ChartsStudio.Domain.Specs;
using AIFlashcardMaker.ChartsStudio.Domain.Themes;

namespace AIFlashcardMaker.ChartsStudio.Application.Rendering;

/// <summary>One figure to draw, at one size, in one resolved style.</summary>
public sealed class RenderRequest
{
    public required FigureSpec Spec { get; init; }

    /// <summary>The context supplying the aggregates. Never a dataset.</summary>
    public required AnalysisContext Context { get; init; }

    /// <summary>
    /// Phase 3 — every visual decision, fully resolved (spec + patch + theme). Null means
    /// "recommendation defaults": the renderer resolves an unpatched style itself, so every
    /// pre-editor call site keeps working and renders exactly as before.
    /// </summary>
    public ResolvedFigureStyle? Style { get; init; }

    public required int WidthPixels { get; init; }
    public required int HeightPixels { get; init; }

    /// <summary>
    /// Scales fonts and line weights with the raster so a large render is the SAME figure
    /// enlarged, not the same figure with microscopic type. 1.0 = screen.
    /// </summary>
    public double ScaleFactor { get; init; } = 1.0;

    /// <summary>
    /// Phase 5 — outer padding baked into the raster/pixel output, in pixels. The figure is
    /// laid out at (Width−2p)×(Height−2p) and composed onto the padded canvas.
    /// </summary>
    public int PaddingPixels { get; init; }

    /// <summary>Phase 5 — transparent canvas for PNG. PDF always flattens (documented).</summary>
    public bool TransparentBackground { get; init; }

    /// <summary>
    /// Identifies this render for caching: spec identity + style identity + output geometry.
    /// The style participates so a patched and an unpatched render of the same spec can never
    /// collide in the cache — the bug that would show a user their edits "not taking".
    /// </summary>
    public string CacheKey =>
        $"{Spec.ToRenderKey()}|s:{Style?.CacheKey ?? ""}|{WidthPixels}x{HeightPixels}@{ScaleFactor:F2}" +
        $"|p:{PaddingPixels}|t:{(TransparentBackground ? 1 : 0)}";
}

/// <summary>Vector output: SVG XML at logical size. The encoder applies physical dimensions.</summary>
public sealed class VectorRenderResult
{
    public string? SvgXml { get; init; }
    public bool Succeeded => !string.IsNullOrEmpty(SvgXml);
    public string FailureReason { get; init; } = "";

    public static VectorRenderResult Success(string svg) => new() { SvgXml = svg };
    public static VectorRenderResult Failure(string reason) => new() { FailureReason = reason };
}

/// <summary>Raw pixel output for embedding (PDF): top-down RGB24 rows, fully opaque.</summary>
public sealed class PixelRenderResult
{
    public byte[]? Rgb24 { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool Succeeded => Rgb24 is { Length: > 0 };
    public string FailureReason { get; init; } = "";

    public static PixelRenderResult Success(byte[] rgb, int w, int h) =>
        new() { Rgb24 = rgb, Width = w, Height = h };
    public static PixelRenderResult Failure(string reason) => new() { FailureReason = reason };
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
    /// Identifies the rendering engine and version, e.g. "ScottPlot 5.1.59". Part of every
    /// export manifest (reproducibility) and of the render cache key (an engine upgrade must
    /// invalidate cached pictures, because the same spec may draw differently).
    /// </summary>
    string RendererVersion { get; }

    /// <summary>
    /// Draws one figure as an encoded raster (PNG). Must never throw for ordinary problems —
    /// a missing variable or an unknown chart type is a Failure result with a reason.
    /// </summary>
    RenderResult Render(RenderRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Phase 5 — draws one figure as SVG at the request's logical size (Width×Height at scale
    /// 1). Physical sizing is the encoder's job, via viewBox: the vector scales losslessly.
    /// </summary>
    VectorRenderResult RenderSvg(RenderRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Phase 5 — draws one figure as raw RGB24 rows at full output resolution, flattened
    /// opaque, for embedding into container formats (PDF).
    /// </summary>
    PixelRenderResult RenderPixels(RenderRequest request, CancellationToken cancellationToken);
}
