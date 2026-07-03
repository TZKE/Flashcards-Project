using System.Globalization;
using System.Text.Json.Serialization;
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
