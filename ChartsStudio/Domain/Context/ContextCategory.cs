namespace AIFlashcardMaker.ChartsStudio.Domain.Context;

/// <summary>
/// Charts Studio Phase 2 — one observed category of a categorical variable, with its count.
///
/// Projected verbatim from Research Lab's FrequencyRowResult. The count is never recomputed
/// here, so a bar chart and the descriptive statistics frequency table can never disagree.
/// </summary>
public sealed class ContextCategory
{
    /// <summary>Raw value as it appears in the data.</summary>
    public string Value { get; init; } = "";

    /// <summary>Human-facing label; falls back to <see cref="Value"/> when absent.</summary>
    public string Label { get; init; } = "";

    /// <summary>Number of observations in this category.</summary>
    public int Count { get; init; }

    /// <summary>What to draw on an axis.</summary>
    public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? Value : Label;

    public override string ToString() => $"{DisplayLabel} ({Count})";
}
