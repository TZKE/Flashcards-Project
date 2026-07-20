using AIFlashcardMaker.ChartsStudio.Domain.ChartTypes;
using AIFlashcardMaker.ChartsStudio.Domain.Context;
using AIFlashcardMaker.ChartsStudio.Domain.Specs;

namespace AIFlashcardMaker.ChartsStudio.Domain.Recommendation;

/// <summary>
/// Charts Studio Phase 2 — one figure the engine proposes, with the reason it was proposed.
///
/// Deterministic fields only. When the AI advisory layer arrives it writes into SEPARATE
/// fields, so the deterministic result stays recoverable and inspectable rather than being
/// overwritten by something that cannot be regression-tested.
/// </summary>
public sealed class FigureCandidate
{
    /// <summary>The figure this candidate would produce, ready to render.</summary>
    public required FigureSpec Spec { get; init; }

    public required ChartTypeDescriptor ChartType { get; init; }

    public required ContextVariable Variable { get; init; }

    /// <summary>Deterministic ranking score. Higher sorts first.</summary>
    public int Score { get; init; }

    /// <summary>
    /// Why this figure suits this variable, written in the project's own terms. Template-
    /// generated and good enough to ship without AI — the advisory layer only rewords it.
    /// </summary>
    public required string Rationale { get; init; }

    /// <summary>
    /// Warnings that must travel with the card rather than being discovered after the user has
    /// invested in the figure — small n, high missingness, many categories.
    /// </summary>
    public IReadOnlyList<string> Cautions { get; init; } = Array.Empty<string>();

    public bool HasCautions => Cautions.Count > 0;

    /// <summary>What the card leads with: the project-specific phrasing, not the chart name.</summary>
    public string HeadlineDisplay => Spec.Title;

    public string SubtitleDisplay
    {
        get
        {
            var parts = new List<string>(2) { ChartType.DisplayName };
            if (Variable.ValidN.HasValue) parts.Add($"n = {Variable.ValidN.Value}");
            return string.Join("  ·  ", parts);
        }
    }

    public override string ToString() => $"{ChartType.Id}:{Variable.Name} ({Score})";
}
