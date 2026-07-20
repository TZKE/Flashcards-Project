using AIFlashcardMaker.ChartsStudio.Domain.Context;

namespace AIFlashcardMaker.ChartsStudio.Domain.ChartTypes;

/// <summary>
/// What research question a chart form answers. Categories are phrased as questions in the UI
/// because a user who does not know what a box plot is does know they want to see a
/// distribution.
/// </summary>
public enum ChartCategory
{
    Distribution,
    Comparison,
    Relationship,
    Composition
}

/// <summary>
/// Why a chart type cannot be used for a given variable — carried alongside the verdict so the
/// UI can always explain itself rather than simply refusing.
/// </summary>
public sealed class ChartApplicability
{
    public bool IsApplicable { get; init; }

    /// <summary>User-facing reason, empty when applicable.</summary>
    public string Reason { get; init; } = "";

    public static ChartApplicability Yes { get; } = new() { IsApplicable = true };

    public static ChartApplicability No(string reason) => new() { IsApplicable = false, Reason = reason };
}

/// <summary>
/// Charts Studio Phase 2 — THE EXTENSION SEAM.
///
/// One chart form, described completely: what it needs, when it applies, what to call it, and
/// how to explain it. Adding Kaplan-Meier, ROC or a forest plot later means adding a descriptor
/// and a renderer branch — the recommendation engine, contact sheet, caching and persistence do
/// not change.
///
/// Descriptors are CODE, not user data. A template is not something the user owns; modelling it
/// as persisted data would mean shipping template definitions inside user files and then having
/// to migrate them.
/// </summary>
public sealed class ChartTypeDescriptor
{
    /// <summary>
    /// Stable identifier persisted inside every FigureSpec. Never rename one of these without a
    /// schema migration — a saved figure refers to its chart type by this string.
    /// </summary>
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required ChartCategory Category { get; init; }

    /// <summary>One line describing what the form shows, in plain language.</summary>
    public required string Description { get; init; }

    /// <summary>When this form is the right choice. Replaces a "difficulty" rating, which
    /// measured nothing useful and pushed users toward worse figures.</summary>
    public required string BestFor { get; init; }

    /// <summary>When it is the wrong choice, and what to use instead.</summary>
    public required string AvoidWhen { get; init; }

    /// <summary>
    /// Decides whether this form can be drawn for a given variable, using ONLY the aggregates
    /// Research Lab has already computed. Returns a reason on refusal.
    /// </summary>
    public required Func<ContextVariable, ChartApplicability> Applies { get; init; }

    /// <summary>
    /// Base ranking weight. Final ordering also accounts for role and data quality — see
    /// FigureRecommendationEngine.
    /// </summary>
    public int BaseScore { get; init; }

    public override string ToString() => $"{Id} ({DisplayName})";
}
