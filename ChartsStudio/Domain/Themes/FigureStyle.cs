using System.Globalization;
using System.Text;
using AIFlashcardMaker.ChartsStudio.Domain.ChartTypes;
using AIFlashcardMaker.ChartsStudio.Domain.Specs;

namespace AIFlashcardMaker.ChartsStudio.Domain.Themes;

/// <summary>
/// Charts Studio Phase 3 — every visual decision for one render, fully resolved.
///
/// The renderer consumes THIS, never a patch: by the time drawing starts there are no nulls,
/// no theme lookups and no invalid values left. All fields are concrete and pre-clamped, so a
/// hostile or corrupted patch can degrade a figure's looks but can never crash a render.
///
/// Colours are hex strings and fonts are names — no ScottPlot types. The style is pure Domain,
/// which is what keeps the charting library confined to the one renderer file.
/// </summary>
public sealed class ResolvedFigureStyle
{
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required string Caption { get; init; }
    public required string XLabel { get; init; }
    public required string YLabel { get; init; }

    public required bool ShowXAxis { get; init; }
    public required bool ShowYAxis { get; init; }
    public required bool ShowGrid { get; init; }
    public required bool ShowLegend { get; init; }

    public required string BackgroundHex { get; init; }
    public required string AxisHex { get; init; }
    public required string GridHex { get; init; }
    public required string SeriesFillHex { get; init; }
    public required string SeriesLineHex { get; init; }

    public required string FontFamily { get; init; }
    public required double TitleFontSize { get; init; }
    public required double AxisFontSize { get; init; }
    public required double TickFontSize { get; init; }
    public required double LineWidth { get; init; }
    public required double MarkerSize { get; init; }
    public required double Opacity { get; init; }

    /// <summary>Horizontal layout. Resolver guarantees this is only true for forms that
    /// support it (bar), so the renderer never has to re-check.</summary>
    public required bool Horizontal { get; init; }

    /// <summary>Per-category colours for bars, or null for single-colour series.</summary>
    public string[]? CategoryPalette { get; init; }

    /// <summary>Contribution to the render cache key. Two resolved styles with the same key
    /// are the same picture; a patched and an unpatched render of one spec must never share a
    /// cache entry.</summary>
    public required string CacheKey { get; init; }
}

/// <summary>
/// Charts Studio Phase 3 — merges spec, patch and theme into a resolved style.
///
/// Precedence, lowest to highest:  theme defaults  →  spec text  →  patch overrides.
///
/// CLAMPING IS THE LAST LINE OF DEFENCE. The editor validates before committing (see
/// FigurePatchValidator), but the resolver assumes nothing: out-of-range numbers are clamped,
/// unparseable colours are ignored, unknown themes fall back. Validation failing must degrade
/// a figure, never kill a render — this is the "validation never crashes rendering" rule made
/// structural.
/// </summary>
public static class FigureStyleResolver
{
    // Shared bounds — the validator rejects outside these, the resolver clamps to them.
    // One source of truth so the two can never disagree about what "valid" means.
    public const double MinFontSize = 6, MaxFontSize = 72;
    public const double MinTickFontSize = 5, MaxTickFontSize = 36;
    public const double MinLineWidth = 0.25, MaxLineWidth = 10;
    public const double MinMarkerSize = 2, MaxMarkerSize = 40;
    public const double MinOpacity = 0.05, MaxOpacity = 1.0;

    public static ResolvedFigureStyle Resolve(FigureSpec spec, FigurePatch? patch)
    {
        var theme = FigureThemes.Get(patch?.ThemeId);

        string seriesFill = ValidHexOrNull(patch?.SeriesColorHex) ?? theme.SeriesFillHex;
        string background = ValidHexOrNull(patch?.BackgroundColorHex) ?? theme.BackgroundHex;

        // Horizontal is only meaningful for bars; resolving it here (not in the renderer)
        // keeps the "where applicable" rule in one testable place.
        bool horizontal =
            string.Equals(patch?.Orientation, "horizontal", StringComparison.OrdinalIgnoreCase)
            && spec.ChartTypeId == ChartTypeRegistry.BarChartId;

        string[]? categoryPalette = null;
        if (patch?.ColorByCategory == true && spec.ChartTypeId == ChartTypeRegistry.BarChartId)
            categoryPalette = FigureThemes.GetPalette(patch.PaletteId).Hexes;

        var style = new ResolvedFigureStyle
        {
            Title = FirstNonBlank(patch?.Title, spec.Title),
            Subtitle = patch?.Subtitle?.Trim() ?? "",
            Caption = patch?.Caption?.Trim() ?? "",
            XLabel = FirstNonBlank(patch?.XAxisTitle, spec.CategoryAxisLabel),
            YLabel = FirstNonBlank(patch?.YAxisTitle, spec.ValueAxisLabel),

            ShowXAxis = patch?.ShowXAxis ?? true,
            ShowYAxis = patch?.ShowYAxis ?? true,
            ShowGrid = patch?.ShowGrid ?? theme.ShowGrid,
            ShowLegend = patch?.ShowLegend ?? false,

            BackgroundHex = background,
            AxisHex = theme.AxisHex,
            GridHex = theme.GridHex,
            SeriesFillHex = seriesFill,
            SeriesLineHex = Darken(seriesFill, 0.6),

            FontFamily = string.IsNullOrWhiteSpace(patch?.FontFamily) ? theme.FontFamily : patch!.FontFamily!.Trim(),
            TitleFontSize = Clamp(patch?.TitleFontSize, theme.TitleFontSize, MinFontSize, MaxFontSize),
            AxisFontSize = Clamp(patch?.AxisFontSize, theme.AxisFontSize, MinFontSize, MaxFontSize),
            TickFontSize = Clamp(patch?.TickFontSize, theme.TickFontSize, MinTickFontSize, MaxTickFontSize),
            LineWidth = Clamp(patch?.LineWidth, theme.LineWidth, MinLineWidth, MaxLineWidth),
            MarkerSize = Clamp(patch?.MarkerSize, theme.MarkerSize, MinMarkerSize, MaxMarkerSize),
            Opacity = Clamp(patch?.Opacity, theme.Opacity, MinOpacity, MaxOpacity),

            Horizontal = horizontal,
            CategoryPalette = categoryPalette,

            // The patch key IS the style identity: same spec + same patch ⇒ same style, and
            // the theme catalogue is code, versioned with the app.
            CacheKey = FigurePatch.KeyOf(patch)
        };

        return style;
    }

    /// <summary>
    /// Phase 5 — a copy of a resolved style with font sizes multiplied. Only the Custom
    /// export profile may carry a non-1 scale (a font multiplier changes layout, which breaks
    /// the "what you approved" guarantee for every named profile). The cache key extends so a
    /// scaled render can never collide with the approved one.
    /// </summary>
    public static ResolvedFigureStyle WithFontScale(ResolvedFigureStyle s, double scale)
    {
        if (Math.Abs(scale - 1.0) < 0.001) return s;
        scale = Math.Clamp(scale, 0.5, 2.0);

        return new ResolvedFigureStyle
        {
            Title = s.Title, Subtitle = s.Subtitle, Caption = s.Caption,
            XLabel = s.XLabel, YLabel = s.YLabel,
            ShowXAxis = s.ShowXAxis, ShowYAxis = s.ShowYAxis,
            ShowGrid = s.ShowGrid, ShowLegend = s.ShowLegend,
            BackgroundHex = s.BackgroundHex, AxisHex = s.AxisHex, GridHex = s.GridHex,
            SeriesFillHex = s.SeriesFillHex, SeriesLineHex = s.SeriesLineHex,
            FontFamily = s.FontFamily,
            TitleFontSize = Math.Clamp(s.TitleFontSize * scale, MinFontSize, MaxFontSize),
            AxisFontSize = Math.Clamp(s.AxisFontSize * scale, MinFontSize, MaxFontSize),
            TickFontSize = Math.Clamp(s.TickFontSize * scale, MinTickFontSize, MaxTickFontSize),
            LineWidth = s.LineWidth, MarkerSize = s.MarkerSize, Opacity = s.Opacity,
            Horizontal = s.Horizontal, CategoryPalette = s.CategoryPalette,
            CacheKey = s.CacheKey + ";fontScale=" +
                       scale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    // ---------------------------------------------------------------------------------

    private static string FirstNonBlank(string? over, string fallback) =>
        string.IsNullOrWhiteSpace(over) ? fallback : over.Trim();

    private static double Clamp(double? value, double fallback, double min, double max)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value)) return fallback;
        return Math.Clamp(value.Value, min, max);
    }

    /// <summary>#RRGGBB only. Anything else — including a plausible-looking near-miss — is
    /// rejected so a corrupt patch can't push garbage into the renderer.</summary>
    public static string? ValidHexOrNull(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        string h = hex.Trim();
        if (h.Length != 7 || h[0] != '#') return null;
        for (int i = 1; i < 7; i++)
            if (!Uri.IsHexDigit(h[i])) return null;
        return h.ToUpperInvariant();
    }

    /// <summary>Derives the outline colour from the fill so a user picking one colour gets a
    /// coherent figure without having to pick two.</summary>
    public static string Darken(string hex, double factor)
    {
        string? valid = ValidHexOrNull(hex);
        if (valid is null) return "#1B4A7A";

        int r = int.Parse(valid.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        int g = int.Parse(valid.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        int b = int.Parse(valid.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        var sb = new StringBuilder("#");
        sb.Append(((int)(r * factor)).ToString("X2", CultureInfo.InvariantCulture));
        sb.Append(((int)(g * factor)).ToString("X2", CultureInfo.InvariantCulture));
        sb.Append(((int)(b * factor)).ToString("X2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
