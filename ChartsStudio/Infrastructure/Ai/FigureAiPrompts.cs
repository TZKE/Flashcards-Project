using System.Text;
using AIFlashcardMaker.ChartsStudio.Domain.Ai;

namespace AIFlashcardMaker.ChartsStudio.Infrastructure.Ai;

/// <summary>
/// Charts Studio Phase 6 — prompt engineering for the figure advisory tasks.
///
/// Charts Studio owns its prompts (Core AI owns only transport). Every prompt built here draws
/// solely on an <see cref="AiFigureContext"/> / <see cref="AiFigureSetContext"/>, which cannot
/// hold raw data — so a prompt structurally cannot leak a dataset. Every prompt also carries the
/// SAFETY RULES: the model may reword and advise, but may not invent a statistic, and for
/// captions may use ONLY the numbers supplied. These are deterministic string builders, pinned
/// by tests that assert the facts are present and the safety rules are stated.
/// </summary>
public static class FigureAiPrompts
{
    /// <summary>The non-negotiable instructions on every request. AI advises; it never invents.</summary>
    public const string SafetyRules =
        "Rules you must follow: use ONLY the numbers explicitly provided; never invent or estimate a " +
        "statistic, count, p-value or sample size; never claim a statistical result or significance; " +
        "if a number is not provided, do not state one. You are giving advice about a figure's " +
        "presentation, not analysing data. Be concise and practical.";

    // ---- Caption ---------------------------------------------------------------------

    public static string CaptionSystem =>
        "You write concise, publication-quality figure captions for medical and scientific papers. "
      + SafetyRules + " "
      + "Return ONLY the caption text — one to three sentences, no label like 'Figure 1', no markdown, "
      + "no commentary before or after.";

    public static string CaptionUser(AiFigureContext f)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Write a caption for this figure.");
        AppendFigureFacts(sb, f);
        if (!string.IsNullOrWhiteSpace(f.ExistingCaption))
            sb.AppendLine($"The author's current caption (improve on it): {f.ExistingCaption}");
        sb.AppendLine("The caption should describe what the figure shows and may state the sample size and the "
                    + "summary statistics listed above, using those exact numbers and no others.");
        return sb.ToString();
    }

    // ---- Critique --------------------------------------------------------------------

    public static string CritiqueSystem =>
        "You are a figure reviewer for scientific publications. " + SafetyRules + " "
      + "Critique the figure's PRESENTATION only (clarity, labelling, chart choice, readability), not its "
      + "statistics. Return ONLY a JSON array of at most 5 objects, each "
      + "{\"severity\":\"info|suggestion|warning\",\"title\":\"short\",\"detail\":\"one or two sentences\"}. "
      + "No markdown, no code fences, no text outside the JSON array.";

    public static string CritiqueUser(AiFigureContext f)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Critique this figure and suggest concrete improvements to how it is presented.");
        AppendFigureFacts(sb, f);
        return sb.ToString();
    }

    // ---- Accessibility (AI prose on top of the deterministic findings) ---------------

    public static string AccessibilitySystem =>
        "You advise on figure accessibility for scientific publication (colourblind-safety, contrast, "
      + "greyscale printing, legibility). " + SafetyRules + " "
      + "Return ONLY a JSON array of at most 4 objects "
      + "{\"severity\":\"info|suggestion|warning\",\"title\":\"short\",\"detail\":\"one or two sentences\"}. "
      + "No markdown, no code fences.";

    public static string AccessibilityUser(AiFigureContext f, IReadOnlyList<AiAdvisoryItem> deterministic)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Advise on the accessibility of this figure for publication.");
        AppendFigureFacts(sb, f);
        if (deterministic.Count > 0)
        {
            sb.AppendLine("Automated checks already found these issues (do not repeat them; add only NEW points):");
            foreach (var d in deterministic) sb.AppendLine($"- {d.Title}");
        }
        return sb.ToString();
    }

    // ---- Consistency (AI prose on top of the deterministic findings) -----------------

    public static string ConsistencySystem =>
        "You advise on whether a set of figures looks like one coherent set for a paper. " + SafetyRules + " "
      + "Return ONLY a JSON array of at most 4 objects "
      + "{\"severity\":\"info|suggestion|warning\",\"title\":\"short\",\"detail\":\"one or two sentences\"}. "
      + "No markdown, no code fences.";

    public static string ConsistencyUser(AiFigureSetContext set, IReadOnlyList<AiAdvisoryItem> deterministic)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Review these {set.Count} figures as a set for the paper \"{set.StudyTitle}\".");
        foreach (var f in set.Figures)
            sb.AppendLine($"Figure {f.Index}: {f.ChartTypeName} — \"{f.Title}\"; font {f.FontFamily}; "
                        + $"{(f.ShowGrid ? "grid" : "no grid")}; {f.PaletteName}.");
        if (deterministic.Count > 0)
        {
            sb.AppendLine("Automated checks already found (do not repeat; add only NEW points):");
            foreach (var d in deterministic) sb.AppendLine($"- {d.Title}");
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------------------------

    private static void AppendFigureFacts(StringBuilder sb, AiFigureContext f)
    {
        sb.AppendLine($"Chart type: {f.ChartTypeName}.");
        sb.AppendLine($"Variable: {f.VariableName}{(string.IsNullOrWhiteSpace(f.Units) ? "" : $" ({f.Units})")}.");
        if (!string.IsNullOrWhiteSpace(f.ValidN)) sb.AppendLine($"Sample size (n): {f.ValidN}.");
        if (f.SummaryFacts.Count > 0) sb.AppendLine("Summary statistics: " + string.Join(", ", f.SummaryFacts) + ".");
        if (f.CategoryFacts.Count > 0) sb.AppendLine("Categories: " + string.Join(", ", f.CategoryFacts) + ".");
    }
}
