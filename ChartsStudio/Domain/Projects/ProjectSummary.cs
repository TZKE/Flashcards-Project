namespace AIFlashcardMaker.ChartsStudio.Domain.Projects;

/// <summary>
/// Charts Studio Phase 1 — the cheap, display-only description of one research project, used
/// by the Project Picker.
///
/// This is deliberately NOT an AnalysisContext. Building a full context for every project just
/// to draw a picker card would do real work for projects the user is not going to open. The
/// picker needs six display fields; the session needs the whole snapshot. Two different shapes
/// for two different jobs, so opening the picker stays instant no matter how many projects
/// exist.
/// </summary>
public sealed class ProjectSummary
{
    /// <summary>Research project identifier — the key for opening and for persistence.</summary>
    public string Id { get; init; } = "";

    /// <summary>Project title; falls back to a placeholder when the project is untitled.</summary>
    public string Title { get; init; } = "";

    /// <summary>Specialty, shown as card context.</summary>
    public string Specialty { get; init; } = "";

    /// <summary>Study type, shown as card context.</summary>
    public string StudyType { get; init; } = "";

    /// <summary>When the project was last modified.</summary>
    public DateTime UpdatedAt { get; init; }

    /// <summary>How many variables the Extraction Sheet declares.</summary>
    public int VariableCount { get; init; }

    /// <summary>
    /// Participants the project reports, or null when it has no dataset yet. Null is rendered
    /// as "No dataset" rather than "0", because those mean very different things to a
    /// researcher.
    /// </summary>
    public int? ParticipantCount { get; init; }

    /// <summary>
    /// Saved figures for this project. Always 0 in Phase 1 — figures do not exist yet — but
    /// read through the real persistence path so the count becomes live automatically once
    /// figures are introduced, with no change to the picker.
    /// </summary>
    public int FigureCount { get; init; }

    /// <summary>How many analyses have already been computed.</summary>
    public int ResultCount { get; init; }

    /// <summary>True when this is the project the user had open last.</summary>
    public bool IsLastOpened { get; init; }

    /// <summary>True when the project has no dataset and so cannot support figures yet.</summary>
    public bool HasDataset => ParticipantCount.HasValue;

    // ---- Display helpers (formatting only — no rules live here) -------------------------

    public string TitleDisplay => string.IsNullOrWhiteSpace(Title) ? "Untitled project" : Title;

    public string ParticipantDisplay => ParticipantCount.HasValue
        ? $"{ParticipantCount.Value} participant{(ParticipantCount.Value == 1 ? "" : "s")}"
        : "No dataset";

    public string VariableDisplay =>
        $"{VariableCount} variable{(VariableCount == 1 ? "" : "s")}";

    public string FigureDisplay =>
        FigureCount == 1 ? "1 figure" : $"{FigureCount} figures";

    public string UpdatedDisplay
    {
        get
        {
            var delta = DateTime.Now - UpdatedAt;
            if (delta.TotalMinutes < 1) return "Just now";
            if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes} min ago";
            if (delta.TotalHours < 24) return $"{(int)delta.TotalHours} h ago";
            if (delta.TotalDays < 7) return $"{(int)delta.TotalDays} d ago";
            return UpdatedAt.ToString("d MMM yyyy");
        }
    }

    /// <summary>Secondary line on the card: specialty and study type when present.</summary>
    public string ContextDisplay
    {
        get
        {
            var parts = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(Specialty)) parts.Add(Specialty);
            if (!string.IsNullOrWhiteSpace(StudyType) && StudyType != "Not sure") parts.Add(StudyType);
            return parts.Count == 0 ? "No study details yet" : string.Join("  ·  ", parts);
        }
    }

    /// <summary>Free-text match used by the picker's search box.</summary>
    public bool MatchesSearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        query = query.Trim();

        return TitleDisplay.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Specialty.Contains(query, StringComparison.OrdinalIgnoreCase)
            || StudyType.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
