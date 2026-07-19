using AIFlashcardMaker.ChartsStudio.Domain.Specs;
using AIFlashcardMaker.ChartsStudio.Domain.Themes;

namespace AIFlashcardMaker.ChartsStudio.Domain.Ai;

/// <summary>One figure paired with its resolved style, for set-level analysis.</summary>
public sealed record FigureWithStyle(int Index, FigureSpec Spec, ResolvedFigureStyle Style);

/// <summary>
/// Charts Studio Phase 6 — DETERMINISTIC consistency review across a figure set.
///
/// "Do these figures look like a set?" is a computable question, and this analyzer answers it
/// with no AI: it compares fonts, font sizes, grid usage, background and colour approach across
/// every figure and reports what does not match. This is the deterministic core of the
/// Consistency workflow — the killer feature no other tool offers, precisely because it does
/// not need a model. AI, when available, only phrases the findings.
///
/// A reviewer or supervisor notices "Figure 3 uses a different font" instantly; catching it
/// first, deterministically, is the whole point.
/// </summary>
public static class FigureConsistencyAnalyzer
{
    public static IReadOnlyList<AiAdvisoryItem> Analyze(IReadOnlyList<FigureWithStyle> figures)
    {
        var items = new List<AiAdvisoryItem>();
        if (figures.Count < 2) return items;   // a set of one is trivially consistent

        // Fonts — the most visible inconsistency in a figure panel.
        FlagDistinct(items, figures, f => f.Style.FontFamily, "font",
            "The figures use more than one font family. A figure set normally shares one font; "
          + "pick a single font for all of them.");

        // Title font size — different sizes make a panel look assembled from scraps.
        FlagDistinct(items, figures, f => f.Style.TitleFontSize.ToString("0.#"), "title size",
            "Title text is sized differently across figures. Match the title size so the set reads as one piece of work.");

        FlagDistinct(items, figures, f => f.Style.AxisFontSize.ToString("0.#"), "axis size",
            "Axis label sizes differ across figures. Use one axis size throughout.");

        // Background — a stray transparent/coloured figure among opaque ones.
        FlagDistinct(items, figures, f => f.Style.BackgroundHex.ToUpperInvariant(), "background",
            "Figures do not share one background colour. Keep the background the same across the set.");

        // Grid — some with, some without, reads as inconsistent.
        var withGrid = figures.Count(f => f.Style.ShowGrid);
        if (withGrid > 0 && withGrid < figures.Count)
            items.Add(SetItem("Grid shown on some figures but not others",
                $"{withGrid} of {figures.Count} figures show a grid. Decide on grid or no grid for the whole set."));

        // Colour approach — mixing single-colour and category-coloured figures is usually fine,
        // but a MIX OF DIFFERENT category palettes is not.
        var palettes = figures
            .Where(f => f.Style.CategoryPalette is { Length: > 0 })
            .Select(f => PaletteId(f.Style.CategoryPalette!))
            .Distinct()
            .ToList();
        if (palettes.Count > 1)
            items.Add(SetItem("Different colour palettes across figures",
                "Category figures use more than one palette. Use one palette for the whole set so the same category is the same colour everywhere."));

        return items;
    }

    private static void FlagDistinct(
        List<AiAdvisoryItem> items,
        IReadOnlyList<FigureWithStyle> figures,
        Func<FigureWithStyle, string> selector,
        string label,
        string detail)
    {
        var distinct = figures.Select(selector)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinct.Count > 1)
            items.Add(SetItem($"Inconsistent {label} across the set", detail));
    }

    private static string PaletteId(string[] palette)
    {
        foreach (var p in FigureThemes.AllPalettes)
        {
            bool match = true;
            for (int i = 0; i < Math.Min(palette.Length, p.Hexes.Length); i++)
                if (!string.Equals(palette[i], p.Hexes[i], StringComparison.OrdinalIgnoreCase)) { match = false; break; }
            if (match) return p.Id;
        }
        return string.Join(",", palette);
    }

    private static AiAdvisoryItem SetItem(string title, string detail) => new()
    {
        Severity = AiAdvisorySeverity.Warning,
        Source = AiAdvisorySource.Deterministic,
        FigureIndex = 0,
        Title = title,
        Detail = detail
    };
}
