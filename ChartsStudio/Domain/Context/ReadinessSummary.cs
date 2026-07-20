namespace AIFlashcardMaker.ChartsStudio.Domain.Context;

/// <summary>
/// Charts Studio Phase 1 — how ready a project is for figure work.
///
/// Mirrors the semantics of Research Lab's StatisticsReadinessState (Blocked / NeedsReview /
/// Ready) deliberately, so the two modules speak the same language to the user.
/// </summary>
public enum ContextReadinessState
{
    /// <summary>Figures must not be proposed. Something structural is missing.</summary>
    Blocked = 0,

    /// <summary>Figures may be proposed, but everything carries a review marker.</summary>
    NeedsReview = 1,

    /// <summary>Figures may be proposed normally.</summary>
    Ready = 2
}

/// <summary>
/// The readiness verdict for one project, plus the specific reasons behind it.
///
/// The gate exists because generating a confident-looking publication figure from data the
/// engine distrusts is worse than generating nothing — it launders bad data into something
/// that looks authoritative. Reasons are carried alongside the verdict so the gate can always
/// explain itself and offer a next action, rather than simply refusing.
///
/// PHASE 1 SCOPE: this is a FOUNDATION-level verdict derived from project structure only —
/// does the project have variables, does it report a dataset, has any analysis been run. It is
/// deliberately NOT the full statistical readiness check, which requires a loaded dataset and
/// belongs to Research Lab's engine. A later phase replaces the body of the evaluation while
/// keeping this contract, which is why callers should depend on this type and not on how it
/// was computed.
/// </summary>
public sealed class ReadinessSummary
{
    public ContextReadinessState State { get; init; } = ContextReadinessState.Blocked;

    /// <summary>Specific, user-facing reasons. Empty when fully ready.</summary>
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();

    /// <summary>
    /// True while the verdict comes from project structure alone. Phase 1 always sets this,
    /// and the UI uses it to avoid over-claiming that the data itself has been validated.
    /// </summary>
    public bool IsProvisional { get; init; } = true;

    public bool CanProceed => State != ContextReadinessState.Blocked;

    public string StateDisplay => State switch
    {
        ContextReadinessState.Ready => "Ready",
        ContextReadinessState.NeedsReview => "Needs review",
        _ => "Not ready"
    };

    public static ReadinessSummary Blocked(params string[] reasons) => new()
    {
        State = ContextReadinessState.Blocked,
        Reasons = reasons,
        IsProvisional = true
    };

    public static ReadinessSummary NeedsReview(params string[] reasons) => new()
    {
        State = ContextReadinessState.NeedsReview,
        Reasons = reasons,
        IsProvisional = true
    };

    public static ReadinessSummary Ready() => new()
    {
        State = ContextReadinessState.Ready,
        Reasons = Array.Empty<string>(),
        IsProvisional = true
    };
}
