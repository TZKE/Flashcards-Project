namespace AIFlashcardMaker.ChartsStudio.Domain.Context;

/// <summary>
/// Charts Studio Phase 1 — one previously computed statistical result, projected from Research
/// Lab's SavedComputedResult.
///
/// This is the anchor for figure-to-result binding, which is Charts Studio's differentiator:
/// a figure bound to a result carries the same n, the same test and the same p-value as the
/// analysis it came from, and can be traced back to it. Nothing here is recomputed — every
/// value is carried across verbatim from what Research Lab already calculated.
///
/// Phase 1 stores these only so the session can report how many analyses a project has.
/// Binding itself arrives with the figure model in a later phase.
/// </summary>
public sealed class ContextResult
{
    /// <summary>Identifier of the source SavedComputedResult, for binding and verify-back.</summary>
    public string Id { get; init; } = "";

    /// <summary>Test that produced this result, e.g. "Welch t-test".</summary>
    public string TestName { get; init; } = "";

    /// <summary>Human-readable description of the variables involved.</summary>
    public string Variables { get; init; } = "";

    /// <summary>Valid-n display string exactly as Research Lab rendered it.</summary>
    public string ValidNDisplay { get; init; } = "";

    /// <summary>p-value display string exactly as Research Lab rendered it.</summary>
    public string PValueDisplay { get; init; } = "";

    /// <summary>Effect-size display string exactly as Research Lab rendered it.</summary>
    public string EffectDisplay { get; init; } = "";

    /// <summary>Whether the source result carried a p-value at all.</summary>
    public bool HasPValue { get; init; }

    /// <summary>Whether Research Lab judged this result significant.</summary>
    public bool IsSignificant { get; init; }

    /// <summary>
    /// The Statistics fingerprint captured by Research Lab at the moment this result was
    /// computed (sheet meaning + dataset file hash + target sample size).
    ///
    /// NOTE: staleness is NOT stored on the result — it is DERIVED by comparing this value
    /// against the project's current statistics fingerprint. Phase 1 carries the value across
    /// but deliberately does not judge staleness, because the current fingerprint requires a
    /// loaded dataset. A later phase performs the comparison; storing a judgement here now
    /// would mean persisting a verdict that could silently go wrong.
    /// </summary>
    public string AnalysisFingerprint { get; init; } = "";

    /// <summary>When Research Lab computed this result.</summary>
    public DateTime ComputedAt { get; init; }

    public override string ToString() => $"{TestName}: {Variables}";
}
