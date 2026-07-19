namespace AIFlashcardMaker.ChartsStudio.Domain.Ai;

/// <summary>The advisory workflows Charts Studio offers. Each maps to a requested Phase 6 feature.</summary>
public enum AiAdvisoryTask
{
    /// <summary>Draft a publication caption for one figure.</summary>
    Caption,

    /// <summary>Critique one figure and suggest improvements.</summary>
    Critique,

    /// <summary>Review one figure (or the set) for accessibility.</summary>
    Accessibility,

    /// <summary>Review the whole set for cross-figure consistency.</summary>
    Consistency
}

/// <summary>How important an advisory point is. Ordering, not alarm.</summary>
public enum AiAdvisorySeverity { Info, Suggestion, Warning }

/// <summary>Where an advisory point came from. The distinction is shown to the user: a
/// deterministic finding is a fact Charts Studio computed; an AI finding is a suggestion.</summary>
public enum AiAdvisorySource { Deterministic, Ai }

/// <summary>
/// Charts Studio Phase 6 — one point of advice about a figure or the set.
///
/// Deliberately plain text the user reads. An advisory item NEVER carries a computed number as
/// authoritative — deterministic items quote facts Charts Studio already holds, and AI items
/// are suggestions the user judges. Nothing here is auto-applied to a figure.
/// </summary>
public sealed class AiAdvisoryItem
{
    public required AiAdvisorySeverity Severity { get; init; }
    public required AiAdvisorySource Source { get; init; }

    /// <summary>Short headline, e.g. "Palette is not colourblind-safe".</summary>
    public required string Title { get; init; }

    /// <summary>One or two sentences the user can act on.</summary>
    public required string Detail { get; init; }

    /// <summary>Which figure this refers to (shelf order, 1-based), or 0 for a set-level point.</summary>
    public int FigureIndex { get; init; }

    public override string ToString() => $"[{Severity}/{Source}] {Title}";
}

/// <summary>
/// Charts Studio Phase 6 — the result of one advisory workflow.
///
/// Always usable, even offline: a workflow with a deterministic core (accessibility,
/// consistency) returns its findings whether or not AI ran, and records in
/// <see cref="UsedAi"/> and <see cref="DegradationNote"/> what the model did or did not add.
/// A user is never left with a blank panel and no explanation.
/// </summary>
public sealed class AiAdvisoryResult
{
    public required AiAdvisoryTask Task { get; init; }

    public IReadOnlyList<AiAdvisoryItem> Items { get; init; } = Array.Empty<AiAdvisoryItem>();

    /// <summary>For the caption task: the draft text, for the user to review and apply. Empty
    /// for the review tasks. Never applied automatically.</summary>
    public string DraftText { get; init; } = "";

    /// <summary>True when the model actually contributed to this result.</summary>
    public bool UsedAi { get; init; }

    /// <summary>Set when AI was wanted but unavailable/failed — states what is missing and why,
    /// so the panel explains itself instead of silently degrading.</summary>
    public string DegradationNote { get; init; } = "";

    public bool HasItems => Items.Count > 0;
    public bool HasDraft => !string.IsNullOrWhiteSpace(DraftText);

    public int WarningCount => Items.Count(i => i.Severity == AiAdvisorySeverity.Warning);

    public static AiAdvisoryResult Empty(AiAdvisoryTask task, string note = "") =>
        new() { Task = task, DegradationNote = note };
}
