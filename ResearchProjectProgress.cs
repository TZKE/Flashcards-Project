using System;
using System.Linq;

namespace AIFlashcardMaker;

/// <summary>
/// Phase 10: single source of truth for a research project's workflow progress.
///
/// Progress is DERIVED from the project's persisted artifacts on every evaluation —
/// never from a stored counter that ad-hoc code has to remember to bump (the old
/// approach stalled every project at 45%). The milestone map mirrors the actual
/// implemented Research Lab workflow; each milestone is awarded only when its real
/// artifact exists, so merely opening a page never advances progress, and the same
/// file always yields the same percentage after restarts, re-logins, and updates.
///
/// Milestones (sum = 100):
///   15  Project basics created            (the project exists)
///   10  Study design guidance             (AI recommendations or a research plan)
///   20  Proposal ready                    (drafted in-app or imported)
///   15  Variables defined                 (data-extraction sheet has variables)
///   10  Dataset imported                  (a CSV has been uploaded/summarized)
///   20  Descriptive statistics completed  (first successful analysis recorded)
///   10  Statistical analyses completed    (at least one saved computed result)
///
/// Report/manuscript text and exports are generated on demand and intentionally not
/// persisted inside the project file, so they are NOT milestones — a fully analyzed
/// project legitimately reaches 100% without them (missing optional steps never
/// block completion). Weights are tuned so the legacy fixed values (15 created,
/// 30 plan, 45 proposal) map onto the same derived numbers, which is how existing
/// "stuck at 45%" projects resume advancing without ever appearing to lose progress.
///
/// The stored <see cref="ResearchProject.ProgressPercent"/> is kept as a monotonic
/// high-water mark for display/back-compat: revisiting or even clearing an earlier
/// step never lowers what the user has already seen.
/// </summary>
public static class ResearchProjectProgress
{
    public static int Compute(ResearchProject p)
    {
        if (p is null) return 0;

        int pct = 15;                                                     // basics created
        if (p.Recommendations is not null || p.Plan is not null) pct += 10;   // design guidance
        if (p.ProposalDraft is not null || p.ProposalImported) pct += 20;     // proposal ready
        if (p.Variables is { Count: > 0 }) pct += 15;                         // variables defined
        if (p.CsvSampleSummary is not null) pct += 10;                        // dataset imported
        if (p.DescriptiveStatistics is not null) pct += 20;                   // descriptive stats
        if (p.ComputedResults is { Count: > 0 }) pct += 10;                   // analyses completed
        return Math.Min(100, pct);
    }

    /// <summary>
    /// Refreshes the project's displayed progress: the derived value, floored by the
    /// stored high-water mark so progress never visibly decreases. Returns true when
    /// the stored value changed (caller decides when to persist).
    /// </summary>
    public static bool Touch(ResearchProject p)
    {
        if (p is null) return false;
        int next = Math.Max(Math.Clamp(p.ProgressPercent, 0, 100), Compute(p));
        if (next == p.ProgressPercent) return false;
        p.ProgressPercent = next;
        return true;
    }
}
