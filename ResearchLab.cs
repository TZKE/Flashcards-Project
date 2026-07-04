using System.Globalization;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Data;

namespace AIFlashcardMaker;

// ---------------------------------------------------------------------------
// Research Lab (Phase 1 — shell only)
//
// Local-only data models for the Research Lab module. Kept completely separate
// from the flashcard deck/card schema and persisted to its own JSON file
// (research_projects.json). No AI, statistics, or manuscript logic lives here
// yet — those arrive in later phases.
// ---------------------------------------------------------------------------

public sealed class ResearchLabData
{
    public List<ResearchProject> Projects { get; set; } = new();
}

public sealed class ResearchProject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Specialty { get; set; } = "";
    public string StudyType { get; set; } = "Not sure";
    public string Aim { get; set; } = "";
    public string Population { get; set; } = "";
    public string Setting { get; set; } = "";
    public string TimePeriod { get; set; } = "";
    public string AvailableDataType { get; set; } = "No data yet";
    public List<string> DesiredOutputs { get; set; } = new();
    public string Notes { get; set; } = "";
    public string CurrentStage { get; set; } = "Project created";
    public int ProgressPercent { get; set; } = 15;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // ---- Phase 2 additions ------------------------------------------------
    // All optional and nullable so Phase 1 research_projects.json files load
    // unchanged (missing fields deserialize to null and default safely).
    public ResearchRecommendations? Recommendations { get; set; }
    public ResearchPlan? Plan { get; set; }
    public ResearchProposalDraft? ProposalDraft { get; set; }

    // ---- Phase 2E addition ------------------------------------------------
    // Set true when the student edits the project details while accepted
    // recommendations already exist, so the Overview can gently suggest
    // regenerating the plan. Cleared whenever recommendations are (re)generated
    // or accepted. Optional/defaulted so older files load unchanged.
    public bool DetailsChangedSinceRecommendations { get; set; }

    // ---- Phase 2F addition ------------------------------------------------
    // Set when the student imports an existing proposal. ProposalImported drives
    // the "import first?" prompt and the "based on imported proposal" badge;
    // ImportedProposalText is kept so regenerating recommendations can be based
    // on the existing proposal rather than only the raw project details. Both are
    // optional/defaulted so older files load unchanged.
    public bool ProposalImported { get; set; }
    public string ImportedProposalText { get; set; } = "";

    // ---- Display helpers (not persisted) ----------------------------------
    // Used by the project-card DataTemplate so the XAML stays clean and we
    // avoid culture-sensitive StringFormat surprises.

    [JsonIgnore]
    public string SpecialtyDisplay =>
        string.IsNullOrWhiteSpace(Specialty) ? "General" : Specialty.Trim();

    [JsonIgnore]
    public string StudyTypeDisplay =>
        string.IsNullOrWhiteSpace(StudyType) ? "Not sure" : StudyType.Trim();

    [JsonIgnore]
    public string StageDisplay =>
        string.IsNullOrWhiteSpace(CurrentStage) ? "Project created" : CurrentStage.Trim();

    [JsonIgnore]
    public string ProgressDisplay => $"{ProgressPercent}%";

    [JsonIgnore]
    public string UpdatedDisplay =>
        "Updated " + UpdatedAt.ToLocalTime().ToString("MMM d, yyyy");
}

// ---------------------------------------------------------------------------
// Phase 2 models — AI recommendations, research plan, and proposal draft.
//
// These are *local* data holders only. They are populated by the in-app
// Research AI service (a configurable backend endpoint or a development mock).
// Source is always recorded so the UI can be honest about where content came from.
// ---------------------------------------------------------------------------

public enum ResearchSourceMode
{
    AiGenerated,
    DevelopmentMock,
    Unknown
}

public sealed class ResearchRecommendations
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ResearchSourceMode SourceMode { get; set; } = ResearchSourceMode.AiGenerated;

    public string RecommendedStudyDesign { get; set; } = "";
    public string RefinedResearchTitle { get; set; } = "";
    public string ResearchQuestion { get; set; } = "";
    public string PrimaryObjective { get; set; } = "";
    public List<string> SecondaryObjectives { get; set; } = new();

    public List<ResearchVariableSuggestion> SuggestedVariables { get; set; } = new();
    public List<ResearchAnalysisSuggestion> SuggestedAnalyses { get; set; } = new();

    public List<string> InclusionCriteria { get; set; } = new();
    public List<string> ExclusionCriteria { get; set; } = new();
    public List<string> DataCollectionSuggestions { get; set; } = new();
    public List<string> BiasAndLimitations { get; set; } = new();
    public List<string> EthicsNotes { get; set; } = new();
    public List<string> NextSteps { get; set; } = new();

    // If the service returned unstructured text (not JSON) we keep it verbatim
    // so nothing is lost.
    public string RawAiText { get; set; } = "";

    public bool AcceptedIntoPlan { get; set; }

    // True when there is at least one structured field to display. Used by the
    // UI to decide between the card viewer and the raw-text fallback.
    [JsonIgnore]
    public bool HasStructuredContent =>
        !string.IsNullOrWhiteSpace(RecommendedStudyDesign)
        || !string.IsNullOrWhiteSpace(RefinedResearchTitle)
        || !string.IsNullOrWhiteSpace(ResearchQuestion)
        || !string.IsNullOrWhiteSpace(PrimaryObjective)
        || SecondaryObjectives.Count > 0
        || SuggestedVariables.Count > 0
        || SuggestedAnalyses.Count > 0
        || InclusionCriteria.Count > 0
        || ExclusionCriteria.Count > 0
        || DataCollectionSuggestions.Count > 0
        || BiasAndLimitations.Count > 0
        || EthicsNotes.Count > 0
        || NextSteps.Count > 0;

    [JsonIgnore]
    public string SourceLabel => SourceMode switch
    {
        ResearchSourceMode.AiGenerated => "AI-generated draft — review before use",
        ResearchSourceMode.DevelopmentMock => "Draft (development mode) — review before use",
        _ => "Draft — review before use"
    };
}

public sealed class ResearchVariableSuggestion
{
    public string VariableName { get; set; } = "";
    public string VariableLabel { get; set; } = "";
    public string VariableType { get; set; } = "";
    public string Role { get; set; } = "";   // Exposure / Outcome / Confounder / Demographic / Other
    public string SuggestedCoding { get; set; } = "";
    public string Notes { get; set; } = "";

    [JsonIgnore]
    public string HeaderDisplay =>
        string.IsNullOrWhiteSpace(VariableLabel)
            ? (string.IsNullOrWhiteSpace(VariableName) ? "Variable" : VariableName)
            : VariableLabel;

    [JsonIgnore]
    public string MetaDisplay
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Role)) parts.Add(Role.Trim());
            if (!string.IsNullOrWhiteSpace(VariableType)) parts.Add(VariableType.Trim());
            return parts.Count > 0 ? string.Join("  •  ", parts) : "—";
        }
    }

    [JsonIgnore]
    public string CodingDisplay =>
        string.IsNullOrWhiteSpace(SuggestedCoding) ? "" : "Coding: " + SuggestedCoding.Trim();

    [JsonIgnore]
    public Visibility CodingVisibility =>
        string.IsNullOrWhiteSpace(SuggestedCoding) ? Visibility.Collapsed : Visibility.Visible;

    [JsonIgnore]
    public Visibility NotesVisibility =>
        string.IsNullOrWhiteSpace(Notes) ? Visibility.Collapsed : Visibility.Visible;
}

public sealed class ResearchAnalysisSuggestion
{
    public string AnalysisName { get; set; } = "";
    public string WhenToUse { get; set; } = "";
    public string VariablesNeeded { get; set; } = "";
    public string OutputExpected { get; set; } = "";
    public string Notes { get; set; } = "";

    [JsonIgnore]
    public string HeaderDisplay =>
        string.IsNullOrWhiteSpace(AnalysisName) ? "Analysis" : AnalysisName.Trim();

    [JsonIgnore]
    public string WhenDisplay =>
        string.IsNullOrWhiteSpace(WhenToUse) ? "" : "When to use: " + WhenToUse.Trim();

    [JsonIgnore]
    public Visibility WhenVisibility =>
        string.IsNullOrWhiteSpace(WhenToUse) ? Visibility.Collapsed : Visibility.Visible;

    [JsonIgnore]
    public string VariablesDisplay =>
        string.IsNullOrWhiteSpace(VariablesNeeded) ? "" : "Variables: " + VariablesNeeded.Trim();

    [JsonIgnore]
    public Visibility VariablesVisibility =>
        string.IsNullOrWhiteSpace(VariablesNeeded) ? Visibility.Collapsed : Visibility.Visible;

    [JsonIgnore]
    public string OutputDisplay =>
        string.IsNullOrWhiteSpace(OutputExpected) ? "" : "Expected output: " + OutputExpected.Trim();

    [JsonIgnore]
    public Visibility OutputVisibility =>
        string.IsNullOrWhiteSpace(OutputExpected) ? Visibility.Collapsed : Visibility.Visible;

    [JsonIgnore]
    public Visibility NotesVisibility =>
        string.IsNullOrWhiteSpace(Notes) ? Visibility.Collapsed : Visibility.Visible;
}

// Editable, user-owned research plan. Built once from accepted recommendations,
// then freely edited by the student. Lists are stored as free text (one item
// per line) so editing stays simple in a plain multiline textbox.
public sealed class ResearchPlan
{
    public string FinalTitle { get; set; } = "";
    public string ResearchQuestion { get; set; } = "";
    public string StudyDesign { get; set; } = "";
    public string Aim { get; set; } = "";
    public string PrimaryObjective { get; set; } = "";
    public string SecondaryObjectives { get; set; } = "";
    public string Population { get; set; } = "";
    public string Setting { get; set; } = "";
    public string InclusionCriteria { get; set; } = "";
    public string ExclusionCriteria { get; set; } = "";
    public string MainVariables { get; set; } = "";
    public string SuggestedAnalyses { get; set; } = "";
    public string NextSteps { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class ResearchProposalDraft
{
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ResearchSourceMode SourceMode { get; set; } = ResearchSourceMode.Unknown;
    public bool IsTemplateGenerated { get; set; }

    public string Title { get; set; } = "";
    public string Background { get; set; } = "";
    public string Rationale { get; set; } = "";
    public string Aim { get; set; } = "";
    public string Objectives { get; set; } = "";
    public string Methods { get; set; } = "";
    public string StudyDesign { get; set; } = "";
    public string Setting { get; set; } = "";
    public string Population { get; set; } = "";
    public string InclusionCriteria { get; set; } = "";
    public string ExclusionCriteria { get; set; } = "";
    public string Variables { get; set; } = "";
    public string DataCollection { get; set; } = "";
    public string StatisticalAnalysisPlan { get; set; } = "";
    public string Ethics { get; set; } = "";
    public string Timeline { get; set; } = "";
    public string Limitations { get; set; } = "";
    public string Notes { get; set; } = "";

    // Verbatim paste when the imported text was not structured JSON.
    public string RawText { get; set; } = "";
}

// ---------------------------------------------------------------------------
// Phase 2F — Import Existing Proposal.
//
// ProposalExtractionResult holds everything the Research AI extracted from an
// existing proposal the student pasted or uploaded. It is a *local* data holder
// only. Extraction is strictly read-only over the supplied text: the service is
// instructed never to invent methods, results, p-values, references, or data —
// anything absent is reported through MissingOrWeakSections / Warnings instead
// of being guessed. Nothing here is applied to the project until the student
// reviews it and clicks Apply.
// ---------------------------------------------------------------------------
public sealed class ProposalExtractionResult
{
    public ResearchSourceMode SourceMode { get; set; } = ResearchSourceMode.AiGenerated;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Project details
    public string ExtractedTitle { get; set; } = "";
    public string ExtractedSpecialty { get; set; } = "";
    public string ExtractedStudyDesign { get; set; } = "";
    public string ExtractedResearchQuestion { get; set; } = "";
    public string ExtractedAim { get; set; } = "";
    public string ExtractedPrimaryObjective { get; set; } = "";
    public List<string> ExtractedSecondaryObjectives { get; set; } = new();
    public string ExtractedPopulation { get; set; } = "";
    public string ExtractedSetting { get; set; } = "";
    public string ExtractedTimePeriod { get; set; } = "";

    // Criteria / plan
    public List<string> ExtractedInclusionCriteria { get; set; } = new();
    public List<string> ExtractedExclusionCriteria { get; set; } = new();
    public List<ResearchVariableSuggestion> ExtractedVariables { get; set; } = new();
    public List<ResearchAnalysisSuggestion> ExtractedSuggestedAnalyses { get; set; } = new();
    public List<string> ExtractedDataCollection { get; set; } = new();
    public List<string> ExtractedEthics { get; set; } = new();
    public List<string> ExtractedLimitations { get; set; } = new();
    public string ExtractedTimeline { get; set; } = "";

    // Full proposal sections
    public ExtractedProposalSections ExtractedProposalSections { get; set; } = new();

    // Quality signals
    public List<string> MissingOrWeakSections { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public string ConfidenceSummary { get; set; } = "";

    // ---- Display helpers (not persisted) ----------------------------------
    [JsonIgnore]
    public bool HasAnyContent =>
        !string.IsNullOrWhiteSpace(ExtractedTitle)
        || !string.IsNullOrWhiteSpace(ExtractedResearchQuestion)
        || !string.IsNullOrWhiteSpace(ExtractedAim)
        || !string.IsNullOrWhiteSpace(ExtractedStudyDesign)
        || !string.IsNullOrWhiteSpace(ExtractedPrimaryObjective)
        || ExtractedSecondaryObjectives.Count > 0
        || ExtractedInclusionCriteria.Count > 0
        || ExtractedExclusionCriteria.Count > 0
        || ExtractedVariables.Count > 0
        || ExtractedSuggestedAnalyses.Count > 0
        || ExtractedProposalSections.HasAnyContent;
}

public sealed class ExtractedProposalSections
{
    public string Background { get; set; } = "";
    public string Rationale { get; set; } = "";
    public string Aim { get; set; } = "";
    public string Objectives { get; set; } = "";
    public string Methods { get; set; } = "";
    public string StudyDesign { get; set; } = "";
    public string Setting { get; set; } = "";
    public string Population { get; set; } = "";
    public string InclusionCriteria { get; set; } = "";
    public string ExclusionCriteria { get; set; } = "";
    public string Variables { get; set; } = "";
    public string DataCollection { get; set; } = "";
    public string StatisticalAnalysisPlan { get; set; } = "";
    public string Ethics { get; set; } = "";
    public string Timeline { get; set; } = "";
    public string Limitations { get; set; } = "";

    [JsonIgnore]
    public bool HasAnyContent =>
        !string.IsNullOrWhiteSpace(Background)
        || !string.IsNullOrWhiteSpace(Rationale)
        || !string.IsNullOrWhiteSpace(Aim)
        || !string.IsNullOrWhiteSpace(Objectives)
        || !string.IsNullOrWhiteSpace(Methods)
        || !string.IsNullOrWhiteSpace(StudyDesign)
        || !string.IsNullOrWhiteSpace(Setting)
        || !string.IsNullOrWhiteSpace(Population)
        || !string.IsNullOrWhiteSpace(InclusionCriteria)
        || !string.IsNullOrWhiteSpace(ExclusionCriteria)
        || !string.IsNullOrWhiteSpace(Variables)
        || !string.IsNullOrWhiteSpace(DataCollection)
        || !string.IsNullOrWhiteSpace(StatisticalAnalysisPlan)
        || !string.IsNullOrWhiteSpace(Ethics)
        || !string.IsNullOrWhiteSpace(Timeline)
        || !string.IsNullOrWhiteSpace(Limitations);
}

// Maps a 0-100 progress value to a pixel width for the mini progress bar on a
// project card. ConverterParameter is the full track width in pixels.
public sealed class PercentBarWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double pct = value is int i ? i : 0;
        pct = Math.Max(0, Math.Min(100, pct));

        double track = 160;
        if (parameter is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double p))
            track = p;

        return track * pct / 100.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
