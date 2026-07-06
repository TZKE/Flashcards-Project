using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
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

    // ---- Phase 3 additions (Data Extraction Sheet) ------------------------
    // The variable sheet / data dictionary the student builds before uploading
    // real data. All optional/defaulted so older research_projects.json files
    // load unchanged (a missing Variables array deserializes to an empty list).
    public List<ResearchVariable> Variables { get; set; } = new();
    public DateTime? ExtractionSheetUpdatedAt { get; set; }

    // "Not started" | "Draft" | "Needs review" | "Ready"
    public string ExtractionSheetStatus { get; set; } = "Not started";

    // Optional Google Form link (stored only; never used to scrape private forms).
    public string GoogleFormUrl { get; set; } = "";

    // Local, privacy-safe summary of an uploaded CSV sample. The full CSV is
    // never stored here or sent to the AI — only headers, inferred types, a few
    // sample values, and counts.
    public CsvSampleSummary? CsvSampleSummary { get; set; }

    // Last validation run for the extraction sheet.
    public ExtractionValidationReport? ExtractionValidationReport { get; set; }

    // Stable keys of conflicts/warnings the student has resolved or chosen to
    // ignore, so they do not reappear on the next Validate run or after a
    // restart (unless the underlying variable/column actually changes). Each key
    // is "kind|normalizedIdentity" (see MainWindow.ConflictKey). Optional/
    // defaulted so older research_projects.json files load unchanged.
    public List<string> IgnoredConflictKeys { get; set; } = new();

    // Target sample size, if one was clearly stated in the imported proposal or
    // research plan. Only ever used to compare against an uploaded sample count —
    // never as statistical/sample-size advice.
    public int? TargetSampleSize { get; set; }

    // ---- Phase 4A addition (Descriptive Statistics) ------------------------
    // Latest deterministic descriptive analysis. Numbers are stored at full
    // precision; display formatting is applied at render time. Optional/nullable
    // so older research_projects.json files load unchanged (null = no analysis
    // has been generated yet). The raw dataset itself is NOT stored here — a
    // copy of the uploaded CSV lives in the app data folder per project.
    public DescriptiveStatisticsRecord? DescriptiveStatistics { get; set; }

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

// ---------------------------------------------------------------------------
// Phase 3 — Data Extraction Sheet (variable sheet / data dictionary only).
//
// These models describe the *plan* for data — the variables, their types,
// roles and coding — NOT any statistics, results, or p-values. Nothing here
// computes anything on real data; that is a later phase. All models are plain,
// serializable, and backward compatible.
// ---------------------------------------------------------------------------

// A single row in the extraction sheet. Implements INotifyPropertyChanged so the
// editable DataGrid reflects both user edits and programmatic updates (AI
// generation / fixes / the edit modal).
public sealed class ResearchVariable : INotifyPropertyChanged
{
    private string _variableName = "";
    private string _questionLabel = "";
    private string _variableType = "Unknown";
    private string _measurementLevel = "NotApplicable";
    private string _role = "Unknown";
    private string _coding = "";
    private string _valueLabels = "";
    private string _missingValueRule = "";
    private string _source = "Manual";
    private string _notes = "";
    private bool _isRequired;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    // ---- Phase 3 stabilization: source-column aliases ---------------------
    // Original header text(s) this variable was derived from — e.g. the exact
    // Google Form / questionnaire question wording. Used ONLY to match this
    // variable against uploaded CSV column headers (which, for a Google Forms
    // export, are the full question text). Optional/defaulted so older
    // research_projects.json files load unchanged.
    private List<string> _sourceColumnAliases = new();
    public List<string> SourceColumnAliases
    {
        get => _sourceColumnAliases;
        set => _sourceColumnAliases = value ?? new List<string>();
    }

    public string VariableName { get => _variableName; set => Set(ref _variableName, value); }
    public string QuestionLabel { get => _questionLabel; set => Set(ref _questionLabel, value); }
    public string VariableType { get => _variableType; set => Set(ref _variableType, value); }
    public string MeasurementLevel { get => _measurementLevel; set => Set(ref _measurementLevel, value); }
    public string Role { get => _role; set => Set(ref _role, value); }
    public string Coding { get => _coding; set => Set(ref _coding, value); }
    public string ValueLabels { get => _valueLabels; set => Set(ref _valueLabels, value); }
    public string MissingValueRule { get => _missingValueRule; set => Set(ref _missingValueRule, value); }
    public string Source { get => _source; set => Set(ref _source, value); }
    public string Notes { get => _notes; set => Set(ref _notes, value); }
    public bool IsRequired { get => _isRequired; set => Set(ref _isRequired, value); }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ResearchVariable Clone() => new()
    {
        // New Id on purpose — a clone is a distinct row.
        VariableName = VariableName,
        QuestionLabel = QuestionLabel,
        VariableType = VariableType,
        MeasurementLevel = MeasurementLevel,
        Role = Role,
        Coding = Coding,
        ValueLabels = ValueLabels,
        MissingValueRule = MissingValueRule,
        Source = Source,
        Notes = Notes,
        IsRequired = IsRequired,
        SourceColumnAliases = new List<string>(SourceColumnAliases),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        UpdatedAt = DateTime.UtcNow;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

// Central lists of allowed values for the DataGrid combo columns and the edit
// modal. Kept as strings (not enums) so the sheet serializes to readable JSON
// and older/newer option sets never break deserialization.
public static class ResearchVariableOptions
{
    public static string[] VariableTypes { get; } =
    {
        "Text", "Numeric", "Binary", "Categorical", "Ordinal",
        "Continuous", "Date", "ID", "Unknown"
    };

    public static string[] MeasurementLevels { get; } =
    {
        "Nominal", "Ordinal", "Scale", "NotApplicable"
    };

    public static string[] Roles { get; } =
    {
        "Outcome", "Exposure", "Predictor", "Confounder", "Demographic",
        "Identifier", "Eligibility", "Other", "Unknown"
    };

    public static string[] Sources { get; } =
    {
        "Manual", "AI Recommendation", "Imported Proposal", "Questionnaire",
        "Google Form", "CSV Sample", "Dataset"
    };
}

// Privacy-safe summary of an uploaded CSV. Never holds the full dataset — only
// headers, inferred types, a handful of sample values, and counts.
public sealed class CsvSampleSummary
{
    public string FileName { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int TotalRows { get; set; }
    public int SampledRows { get; set; }
    public List<CsvColumnSummary> Columns { get; set; } = new();

    [JsonIgnore]
    public string HeaderSummary =>
        Columns.Count == 0 ? "No columns" : string.Join(", ", Columns.Select(c => c.Name));

    [JsonIgnore]
    public string CountSummary =>
        $"{Columns.Count} column{(Columns.Count == 1 ? "" : "s")} · {TotalRows} row{(TotalRows == 1 ? "" : "s")}";
}

public sealed class CsvColumnSummary
{
    public string Name { get; set; } = "";
    public string InferredType { get; set; } = "";     // Text/Numeric/Date/Binary/Categorical/Empty
    public int MissingCount { get; set; }
    public int MissingPercent { get; set; }
    public int UniqueCount { get; set; }
    public bool IsLikelyCategorical { get; set; }
    public List<string> SampleValues { get; set; } = new();   // a few example values only

    [JsonIgnore]
    public string Display =>
        $"{Name} — {InferredType}, {UniqueCount} unique, {MissingPercent}% missing";

    [JsonIgnore]
    public string SampleValuesDisplay =>
        SampleValues is { Count: > 0 } ? string.Join(", ", SampleValues) : "—";
}

// One validation run over the extraction sheet. Local only — no statistics.
public sealed class ExtractionValidationReport
{
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> Suggestions { get; set; } = new();

    // Set when the uploaded CSV has fewer rows than the proposal target sample
    // size. This is a HARD blocker for Statistics (an error), so the project is
    // not "ready for the next phase" until a complete CSV is uploaded.
    public bool SampleSizeIncomplete { get; set; }

    // Ready = no hard errors. Warnings/suggestions are advisory.
    public bool IsReady => Errors.Count == 0;

    [JsonIgnore] public int TotalIssues => Errors.Count + Warnings.Count + Suggestions.Count;

    [JsonIgnore]
    public string StatusText => IsReady
        ? (Warnings.Count == 0 ? "Ready for the next phase" : "Ready, with some warnings to review")
        : $"{Errors.Count} issue{(Errors.Count == 1 ? "" : "s")} to fix before continuing";
}

// Result of an AI extraction-sheet generation or fix pass. Carries the proposed
// variable rows plus advisory notes. Never contains data, statistics, or results.
public sealed class ExtractionSheetResult
{
    public ResearchSourceMode SourceMode { get; set; } = ResearchSourceMode.AiGenerated;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<ResearchVariable> Variables { get; set; } = new();
    public List<string> MissingExpectedVariables { get; set; } = new();
    public List<string> ExtraOrUnexplainedColumns { get; set; } = new();
    public List<string> ChangeSummary { get; set; } = new();   // populated by the Fix pass
    public List<string> Warnings { get; set; } = new();
    public string ConfidenceSummary { get; set; } = "";
    public string TargetSampleSizeText { get; set; } = "";     // free text if the AI spotted one

    [JsonIgnore]
    public bool HasAnyContent =>
        Variables.Count > 0
        || MissingExpectedVariables.Count > 0
        || ExtraOrUnexplainedColumns.Count > 0
        || ChangeSummary.Count > 0;
}

// ---------------------------------------------------------------------------
// Structured "Fix with Research AI" proposals (Phase 3 final stabilization).
// The AI no longer returns a rewritten sheet; it returns ONE proposal PER
// active conflict, which the student reviews (accept / edit / delete) before
// anything is applied. Never contains data, samples, statistics, or results.
// ---------------------------------------------------------------------------

// Compact description of one active conflict sent TO the AI (key + titles only,
// no raw CSV rows, no proposal text).
public sealed class ConflictFixInput
{
    public string ConflictKey { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string VariableName { get; set; } = "";
    public string ColumnName { get; set; } = "";
}

// One proposed fix returned BY the AI for one conflict. Action is one of:
// add_variable | map_csv_column_to_variable | rename_variable | add_alias |
// update_coding | mark_resolved | ignore | no_safe_fix.
public sealed class ConflictFixProposal : INotifyPropertyChanged
{
    public string ConflictKey { get; set; } = "";
    public string ConflictTitle { get; set; } = "";
    public string Action { get; set; } = "no_safe_fix";
    public string Explanation { get; set; } = "";
    public string Confidence { get; set; } = "low";        // high | medium | low
    public string TargetVariable { get; set; } = "";
    public string TargetColumn { get; set; } = "";

    // Triage bucket returned by the AI (or derived from Action if it omitted one):
    // "safe_fix" | "safe_ignore" | "manual_review" | "no_safe_fix". Drives the
    // grouped review UI and the default selection.
    public string Category { get; set; } = "";

    private static readonly string[] ApplicableActions =
    {
        "add_variable", "map_csv_column_to_variable", "rename_variable",
        "add_alias", "update_coding", "mark_resolved", "ignore"
    };

    // Effective category: honor an explicit AI category, else derive from action.
    [JsonIgnore]
    public string EffectiveCategory
    {
        get
        {
            string c = (Category ?? "").Trim().ToLowerInvariant();
            if (c is "safe_fix" or "safe_ignore" or "manual_review" or "no_safe_fix") return c;
            return Action switch
            {
                "ignore" => "safe_ignore",
                "no_safe_fix" or "" => "no_safe_fix",
                _ when ApplicableActions.Contains(Action) => "safe_fix",
                _ => "no_safe_fix"
            };
        }
    }

    [JsonIgnore] public int CategoryOrder => EffectiveCategory switch { "safe_fix" => 0, "safe_ignore" => 1, "manual_review" => 2, _ => 3 };
    [JsonIgnore]
    public string CategoryDisplay => EffectiveCategory switch
    {
        "safe_fix" => "Safe fixes",
        "safe_ignore" => "Routine — safe to ignore",
        "manual_review" => "Needs manual review",
        _ => "No safe fix"
    };

    // The single user-editable value the action needs (new name for rename,
    // alias text for add_alias/map, coding for update_coding, variable name for
    // add_variable). Bound TwoWay in the review list so the student can edit
    // each fix before applying.
    private string _proposedValue = "";
    public string ProposedValue
    {
        get => _proposedValue;
        set { if (_proposedValue != value) { _proposedValue = value; Raise(nameof(ProposedValue)); } }
    }

    private bool _accepted;
    public bool Accepted
    {
        get => _accepted;
        set { if (_accepted != value) { _accepted = value; Raise(nameof(Accepted)); } }
    }

    // Only safe_fix / safe_ignore proposals with a real action can be applied;
    // manual_review and no_safe_fix are shown for reference only.
    [JsonIgnore]
    public bool IsApplicable =>
        EffectiveCategory is "safe_fix" or "safe_ignore"
        && Action is not ("no_safe_fix" or "")
        && ApplicableActions.Contains(Action);

    // Default checkbox state: high-confidence safe fixes and safe ignores are
    // pre-selected; everything else (lower confidence, manual review) is not.
    [JsonIgnore] public bool DefaultSelected => IsApplicable && Confidence == "high";

    [JsonIgnore]
    public string ActionDisplay => Action switch
    {
        "add_variable" => "Add variable",
        "map_csv_column_to_variable" => "Map CSV column",
        "rename_variable" => "Rename variable",
        "add_alias" => "Add alias",
        "update_coding" => "Update coding",
        "mark_resolved" => "Mark resolved",
        "ignore" => "Safe to ignore",
        _ => "Needs manual review"
    };
    [JsonIgnore]
    public string TargetDisplay =>
        !string.IsNullOrWhiteSpace(TargetVariable) && !string.IsNullOrWhiteSpace(TargetColumn)
            ? $"{TargetVariable} ↔ {TargetColumn}"
            : !string.IsNullOrWhiteSpace(TargetVariable) ? TargetVariable
            : !string.IsNullOrWhiteSpace(TargetColumn) ? TargetColumn : "—";
    [JsonIgnore] public string ConfidenceDisplay => Confidence switch { "high" => "High", "medium" => "Medium", _ => "Low" };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// The full AI conflict-fix response: one proposal per conflict it addressed.
public sealed class ConflictFixResult
{
    public List<ConflictFixProposal> Fixes { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

// One row of the "Research AI is working" progress checklist. State drives the
// glyph/colors via DataTriggers: "Pending" | "Current" | "Done".
public sealed class AiWorkStep : INotifyPropertyChanged
{
    public AiWorkStep(string label) => Label = label;

    public string Label { get; }

    private string _state = "Pending";
    public string State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Glyph)));
        }
    }

    [JsonIgnore] public string Glyph => State switch { "Done" => "✓", "Current" => "●", _ => "○" };

    public event PropertyChangedEventHandler? PropertyChanged;
}

// One difference between the extraction sheet, the uploaded CSV sample, and the
// plan, shown as a card in the Resolve Conflicts window. Not persisted — rebuilt
// from project state each time. The Visibility flags drive which action buttons
// the card offers; every conflict can also simply be ignored.
public sealed class ExtractionConflict : INotifyPropertyChanged
{
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";

    // "Error" | "Warning" | "Suggestion" — colors the severity chip.
    public string Severity { get; set; } = "Warning";
    // "Sheet" | "CSV" | "Proposal" | "Recommendations" — where the conflict came from.
    public string Source { get; set; } = "Sheet";

    public ResearchVariable? Variable { get; set; }
    public CsvColumnSummary? Column { get; set; }

    // For "match with existing": candidate sheet variables to align with the
    // CSV column; the ComboBox writes the user's pick into SelectedMatch.
    public List<string> MatchCandidates { get; set; } = new();
    public string? SelectedMatch { get; set; }

    public Visibility AddVis { get; set; } = Visibility.Collapsed;
    public Visibility MatchVis { get; set; } = Visibility.Collapsed;
    public Visibility RenameVis { get; set; } = Visibility.Collapsed;
    public Visibility DeleteVis { get; set; } = Visibility.Collapsed;
    public Visibility EditVis { get; set; } = Visibility.Collapsed;

    // ---- Staged manual resolution (Phase 3 polish) ------------------------
    // A conflict action stages a decision; nothing is applied to the sheet
    // until "Save and Close". Cancel discards all staged decisions.
    // StagedAction: "" | "Add" | "Match" | "Delete" | "Ignore" | "MarkResolved".
    public string StagedAction { get; set; } = "";

    private string _status = "Pending";   // "Pending" | "Resolved" | "Ignored" | "Needs review"
    public string Status
    {
        get => _status;
        set { if (_status != value) { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
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
