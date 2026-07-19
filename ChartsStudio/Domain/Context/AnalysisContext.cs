namespace AIFlashcardMaker.ChartsStudio.Domain.Context;

/// <summary>
/// Charts Studio Phase 1 — THE boundary contract between Research Lab and Charts Studio.
///
/// This is the complete, immutable, read-only snapshot of one research project as Charts Studio
/// sees it. It is the single input to everything downstream: recommendation, rendering,
/// captions, export.
///
/// WHY A SNAPSHOT AND NOT A REFERENCE
/// Charts Studio must never re-read a CSV, never re-derive a variable type, and never recompute
/// a statistic. That is not a stylistic preference. Commit dbaeed5 spent an entire release
/// eliminating exactly one class of bug: two independent readers of the same data that
/// disagreed, producing wrong numbers with no error shown to the user. A charting module that
/// re-interpreted the dataset would reintroduce that failure mode in the one place where the
/// output is a published figure.
///
/// THE RULE
///   Research Lab produces the context.  Charts Studio consumes it.
///   One direction, one shape, one version stamp.
///
/// Everything in here is populated by AnalysisContextProvider, which is the ONLY code in the
/// module permitted to touch Research Lab types.
/// </summary>
public sealed class AnalysisContext
{
    /// <summary>Identifier of the source research project. The only link back.</summary>
    public string ProjectId { get; init; } = "";

    /// <summary>Project title at the moment the snapshot was taken.</summary>
    public string ProjectTitle { get; init; } = "";

    /// <summary>
    /// Content-derived identity of the inputs behind this snapshot. Any figure records the
    /// fingerprint it was built from; a mismatch on reload means that figure is stale.
    /// </summary>
    public ContextFingerprint Fingerprint { get; init; } = ContextFingerprint.None;

    /// <summary>When this snapshot was assembled (local time, for display).</summary>
    public DateTime CapturedAt { get; init; } = DateTime.Now;

    /// <summary>Every variable the project declares, projected for charting.</summary>
    public IReadOnlyList<ContextVariable> Variables { get; init; } = Array.Empty<ContextVariable>();

    /// <summary>Every statistical result the project has already computed.</summary>
    public IReadOnlyList<ContextResult> Results { get; init; } = Array.Empty<ContextResult>();

    /// <summary>Study shape and stated intent.</summary>
    public ContextDesign Design { get; init; } = new();

    /// <summary>Whether figure work may proceed, and why not when it may not.</summary>
    public ReadinessSummary Readiness { get; init; } = ReadinessSummary.Blocked("Context not loaded.");

    /// <summary>
    /// Participant / row count the project reports, when it reports one. Null means the
    /// project has no dataset summary yet. Charts Studio never counts rows itself.
    /// </summary>
    public int? ParticipantCount { get; init; }

    /// <summary>True when the project reports an imported dataset.</summary>
    public bool HasDataset => ParticipantCount.HasValue;

    // ---- Convenience projections used by the studio header and, later, by ranking --------

    public int VariableCount => Variables.Count;

    public int ChartableVariableCount => Variables.Count(v => v.IsChartable);

    public int ContinuousCount => Variables.Count(v => v.Kind == ContextVariableKind.Continuous);

    public int CategoricalCount => Variables.Count(v =>
        v.Kind is ContextVariableKind.Binary or ContextVariableKind.Nominal or ContextVariableKind.Ordinal);

    public int ExcludedCount => Variables.Count(v => !v.IsChartable);

    public int ResultCount => Results.Count;

    /// <summary>An explicit empty context, used before a project is opened.</summary>
    public static AnalysisContext None { get; } = new()
    {
        Readiness = ReadinessSummary.Blocked("No project is open.")
    };

    public bool IsLoaded => !string.IsNullOrEmpty(ProjectId);
}
