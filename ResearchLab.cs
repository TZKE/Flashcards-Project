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
// These are *local* data holders only. Nothing here calls a network API. They
// are populated either from a manual copy/paste Claude workflow or from the
// offline draft service. Source is always recorded so the UI can be honest
// about where the content came from.
// ---------------------------------------------------------------------------

public enum ResearchSourceMode
{
    ManualClaude,
    OfflineMock,
    FutureApi
}

public sealed class ResearchRecommendations
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ResearchSourceMode SourceMode { get; set; } = ResearchSourceMode.ManualClaude;

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

    // When the pasted text was not structured JSON we keep it verbatim so the
    // student never loses what Claude wrote.
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
        ResearchSourceMode.ManualClaude => "Imported from Claude (manual)",
        ResearchSourceMode.OfflineMock => "Offline draft — not AI generated",
        ResearchSourceMode.FutureApi => "Generated via API",
        _ => "Imported"
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

    public ResearchSourceMode SourceMode { get; set; } = ResearchSourceMode.OfflineMock;
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
