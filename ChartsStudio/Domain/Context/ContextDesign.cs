namespace AIFlashcardMaker.ChartsStudio.Domain.Context;

/// <summary>
/// Charts Studio Phase 1 — the study's shape and intent, projected from the research project.
///
/// Recommendation ranking (a later phase) leans on this heavily: study design constrains which
/// figures are appropriate, and the stated objective drives which of the valid figures matter
/// most. Phase 1 only carries it into the session so the studio header can prove the project
/// was understood.
///
/// Design classification is carried across as the project declared it. Charts Studio does not
/// re-classify: Research Lab already owns that logic (see TwoByTwoStudyDesignKind and its
/// deliberately conservative ClassifyDesign), and a second classifier would be free to disagree.
/// </summary>
public sealed class ContextDesign
{
    /// <summary>Study type string exactly as the project declares it.</summary>
    public string StudyType { get; init; } = "";

    /// <summary>Clinical or research specialty, used for display only.</summary>
    public string Specialty { get; init; } = "";

    /// <summary>The project's stated aim.</summary>
    public string Aim { get; init; } = "";

    /// <summary>Refined research question, when the project has progressed far enough to have one.</summary>
    public string ResearchQuestion { get; init; } = "";

    /// <summary>Primary objective, when recorded.</summary>
    public string PrimaryObjective { get; init; } = "";

    /// <summary>Secondary objectives, when recorded.</summary>
    public IReadOnlyList<string> SecondaryObjectives { get; init; } = Array.Empty<string>();

    /// <summary>Population under study, for display and for later caption composition.</summary>
    public string Population { get; init; } = "";

    /// <summary>True when the project has enough stated intent to rank figures by relevance.</summary>
    public bool HasStatedObjective =>
        !string.IsNullOrWhiteSpace(PrimaryObjective)
        || !string.IsNullOrWhiteSpace(ResearchQuestion)
        || !string.IsNullOrWhiteSpace(Aim);
}
