namespace AIFlashcardMaker.ChartsStudio.Domain.Context;

/// <summary>
/// Charts Studio Phase 1 — how Charts Studio classifies a variable for charting purposes.
///
/// IMPORTANT: this mirrors the SEMANTICS of Research Lab's existing RecoVarKind
/// (ResearchLabTestRecommendations.cs) but is a separate, module-owned type. Charts Studio
/// consumes a projection of Research Lab's interpretation and must never become a second
/// source of truth for it.
///
/// The kind is derived from the Extraction Sheet's declared VariableType / MeasurementLevel,
/// mirroring exactly the rules in ResearchLabStatistics.Prepare — whose own comment states the
/// principle plainly: "Kind from the SHEET (source of truth), never from the CSV." So the kind
/// here is authoritative, not provisional, and does not change when a dataset arrives.
///
/// What a dataset DOES add is observed detail: valid/missing counts and how many categories
/// actually appear. Those stay null until then, guarded by
/// <see cref="ContextVariable.IsObservedDataAvailable"/>. One consequence worth knowing: a
/// variable declared "Categorical" is read as Nominal until observation can confirm it has
/// exactly two levels — declaring Binary is the only way to assert that from the sheet alone.
/// </summary>
public enum ContextVariableKind
{
    /// <summary>Continuous / scale numeric.</summary>
    Continuous,

    /// <summary>Categorical family, declared or observed as exactly two levels.</summary>
    Binary,

    /// <summary>Categorical family, unordered, three or more levels.</summary>
    Nominal,

    /// <summary>Ordered categories.</summary>
    Ordinal,

    /// <summary>Free text, dates, identifiers — not chartable.</summary>
    Unsupported,

    /// <summary>Metadata unclear or absent — must be reviewed before charting.</summary>
    Ambiguous
}

/// <summary>
/// How a variable's declared Extraction-Sheet role maps onto charting intent. Mirrors the
/// semantics of Research Lab's RecoRoleClass.
/// </summary>
public enum ContextVariableRole
{
    /// <summary>Predictor / exposure / group / independent variable.</summary>
    Predictor,

    /// <summary>Outcome / dependent variable.</summary>
    Outcome,

    /// <summary>Blank / unknown / demographic — confirm before relying on it.</summary>
    Unclear,

    /// <summary>Identifier / metadata — never charted.</summary>
    Excluded
}

/// <summary>
/// One variable as Charts Studio sees it: a read-only PROJECTION of a Research Lab
/// ResearchVariable, never a reference to it.
///
/// The projection is deliberate. If Charts Studio held live references into Research Lab's
/// objects, a change there would silently change figures here. A projection plus a
/// fingerprint makes change *detectable* instead of invisible — which is the entire
/// staleness design in one decision.
/// </summary>
public sealed class ContextVariable
{
    /// <summary>Stable identifier carried over from the source variable.</summary>
    public string Id { get; init; } = "";

    /// <summary>Machine-ish name as declared in the Extraction Sheet.</summary>
    public string Name { get; init; } = "";

    /// <summary>Human-facing label; falls back to <see cref="Name"/> when absent.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Declared type string, verbatim from the Extraction Sheet.</summary>
    public string DeclaredType { get; init; } = "";

    /// <summary>Declared measurement level string, verbatim from the Extraction Sheet.</summary>
    public string DeclaredLevel { get; init; } = "";

    /// <summary>Declared role string, verbatim from the Extraction Sheet.</summary>
    public string DeclaredRole { get; init; } = "";

    /// <summary>Charting classification derived from the declared metadata.</summary>
    public ContextVariableKind Kind { get; init; } = ContextVariableKind.Ambiguous;

    /// <summary>Charting role derived from the declared role string.</summary>
    public ContextVariableRole Role { get; init; } = ContextVariableRole.Unclear;

    /// <summary>Units, when the project records them. Used for axis labels later.</summary>
    public string Units { get; init; } = "";

    /// <summary>
    /// False in Phase 1 for every variable: no dataset has been read, so counts and observed
    /// category levels are unknown. A later phase sets this true and fills the fields below
    /// from Research Lab's prepared values.
    /// </summary>
    public bool IsObservedDataAvailable { get; init; }

    /// <summary>Non-missing observation count. Null until observed data is available.</summary>
    public int? ValidN { get; init; }

    /// <summary>Missing observation count. Null until observed data is available.</summary>
    public int? MissingN { get; init; }

    /// <summary>Observed distinct category count. Null until observed data is available.</summary>
    public int? ObservedCategoryCount { get; init; }

    /// <summary>True when this variable can never carry a figure (identifier, free text).</summary>
    public bool IsChartable =>
        Kind is not (ContextVariableKind.Unsupported or ContextVariableKind.Ambiguous)
        && Role != ContextVariableRole.Excluded;

    public override string ToString() => $"{DisplayName} ({Kind})";
}
