using System.Globalization;
using AIFlashcardMaker.ChartsStudio.Domain.Specs;
using AIFlashcardMaker.ChartsStudio.Domain.Themes;

namespace AIFlashcardMaker.ChartsStudio.Domain.Ai;

/// <summary>
/// Charts Studio Phase 6 — DETERMINISTIC accessibility review.
///
/// Whether a figure is colourblind-safe, greyscale-safe or legible at print size is a
/// COMPUTABLE fact, not a matter of opinion — so Charts Studio computes it, with no AI. This
/// analyzer is the deterministic core of the Accessibility workflow; AI, when available, only
/// adds prose on top. It runs fully offline and is pinned by unit tests.
///
/// The guiding principle from the design docs: for a publication tool, accessibility and
/// print-safety are the SAME requirement — greyscale-safe encoding is both an accessibility win
/// and a submission requirement, because many journals still print mono.
/// </summary>
public static class FigureAccessibilityAnalyzer
{
    // WCAG-style relative-luminance contrast floors. 3.0 is the large-graphics threshold;
    // below it a fill is hard to tell from a white page in print or for low-vision readers.
    private const double MinContrastAgainstBackground = 2.0;

    // Below this tick size (points) axis numbers risk illegibility once a figure is shrunk to a
    // journal column. Conservative — a warning, not a hard rule.
    private const double MinReadableTickPt = 6.0;

    public static IReadOnlyList<AiAdvisoryItem> Analyze(int index, FigureSpec spec, ResolvedFigureStyle style)
    {
        var items = new List<AiAdvisoryItem>();

        // 1. Colourblind-safety of a category palette.
        if (style.CategoryPalette is { Length: > 1 } palette)
        {
            bool isKnownSafe = PaletteMatches(palette, "colorblind-safe") || PaletteMatches(palette, "grayscale");
            if (!isKnownSafe)
                items.Add(new AiAdvisoryItem
                {
                    Severity = AiAdvisorySeverity.Warning,
                    Source = AiAdvisorySource.Deterministic,
                    FigureIndex = index,
                    Title = "Palette may not be colourblind-safe",
                    Detail = "This figure colours categories with a palette that is not the colourblind-safe set. "
                           + "Switch to the Colourblind-safe palette so readers with colour-vision deficiency can tell the categories apart."
                });

            // Adjacent-colour separation: colours too close in luminance read as one in greyscale.
            for (int i = 1; i < palette.Length; i++)
            {
                double l1 = RelativeLuminance(palette[i - 1]);
                double l2 = RelativeLuminance(palette[i]);
                if (Math.Abs(l1 - l2) < 0.10 && !isKnownSafe)
                {
                    items.Add(new AiAdvisoryItem
                    {
                        Severity = AiAdvisorySeverity.Suggestion,
                        Source = AiAdvisorySource.Deterministic,
                        FigureIndex = index,
                        Title = "Some categories will merge in greyscale",
                        Detail = "Two palette colours have almost the same brightness, so they will look identical if the journal prints in black and white. "
                               + "Prefer a palette that also varies brightness, or distinguish categories by pattern."
                    });
                    break;
                }
            }
        }

        // 2. Series-vs-background contrast (single-colour figures).
        if (style.CategoryPalette is null or { Length: 0 })
        {
            double contrast = ContrastRatio(style.SeriesFillHex, style.BackgroundHex);
            if (contrast < MinContrastAgainstBackground)
                items.Add(new AiAdvisoryItem
                {
                    Severity = AiAdvisorySeverity.Warning,
                    Source = AiAdvisorySource.Deterministic,
                    FigureIndex = index,
                    Title = "Low contrast against the background",
                    Detail = $"The series colour is close in brightness to the background (contrast ≈ {contrast:0.0}:1). "
                           + "A darker or more saturated colour reads more reliably in print and for low-vision readers."
                });
        }

        // 3. Tick legibility at print size.
        if (style.ShowXAxis || style.ShowYAxis)
        {
            if (style.TickFontSize < MinReadableTickPt)
                items.Add(new AiAdvisoryItem
                {
                    Severity = AiAdvisorySeverity.Suggestion,
                    Source = AiAdvisorySource.Deterministic,
                    FigureIndex = index,
                    Title = "Axis numbers may be too small",
                    Detail = $"Tick labels are {style.TickFontSize:0.#} pt. Once the figure is reduced to a journal column they may be hard to read; "
                           + "consider a larger tick size."
                });
        }

        // 4. A category figure with a colour palette but no legend is unreadable.
        if (style.CategoryPalette is { Length: > 1 } && !style.ShowLegend)
            items.Add(new AiAdvisoryItem
            {
                Severity = AiAdvisorySeverity.Suggestion,
                Source = AiAdvisorySource.Deterministic,
                FigureIndex = index,
                Title = "Coloured categories without a legend",
                Detail = "The categories are distinguished by colour but no legend is shown, so a reader cannot tell which colour is which. "
                       + "Turn on the legend, or label the categories on the axis."
            });

        return items;
    }

    // ---------------------------------------------------------------------------------
    // Colour maths (deterministic, WCAG relative luminance)
    // ---------------------------------------------------------------------------------

    private static bool PaletteMatches(string[] palette, string paletteId)
    {
        var known = FigureThemes.GetPalette(paletteId).Hexes;
        if (palette.Length == 0) return false;
        // A figure's palette "is" the named one if its first colours come from it in order —
        // the resolver assigns them cyclically, so a prefix match is exact identification.
        for (int i = 0; i < Math.Min(palette.Length, known.Length); i++)
            if (!string.Equals(palette[i], known[i], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    public static double ContrastRatio(string a, string b)
    {
        double la = RelativeLuminance(a) + 0.05;
        double lb = RelativeLuminance(b) + 0.05;
        return la > lb ? la / lb : lb / la;
    }

    public static double RelativeLuminance(string hex)
    {
        if (!TryRgb(hex, out int r, out int g, out int b)) return 0;
        double R = Channel(r / 255.0), G = Channel(g / 255.0), B = Channel(b / 255.0);
        return 0.2126 * R + 0.7152 * G + 0.0722 * B;
    }

    private static double Channel(double c) => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static bool TryRgb(string hex, out int r, out int g, out int b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        string h = hex.Trim();
        if (h.Length != 7 || h[0] != '#') return false;
        try
        {
            r = int.Parse(h.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            g = int.Parse(h.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            b = int.Parse(h.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return true;
        }
        catch { return false; }
    }
}
