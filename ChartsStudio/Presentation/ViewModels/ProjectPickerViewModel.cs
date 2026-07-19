using System.Collections.ObjectModel;
using AIFlashcardMaker.ChartsStudio.Domain.Projects;

namespace AIFlashcardMaker.ChartsStudio.Presentation.ViewModels;

/// <summary>
/// Charts Studio Phase 1 — the Project Picker.
///
/// Holds the full project list and the filtered view the cards bind to. Filtering happens in
/// memory over already-built summaries, which is what keeps typing in the search box instant:
/// no project is re-read and no context is assembled while searching.
/// </summary>
public sealed class ProjectPickerViewModel : ObservableObject
{
    private IReadOnlyList<ProjectSummary> _all = Array.Empty<ProjectSummary>();
    private string _searchQuery = "";

    /// <summary>The cards currently visible, after search filtering.</summary>
    public ObservableCollection<ProjectSummary> VisibleProjects { get; } = new();

    /// <summary>Raised when the user activates a project card.</summary>
    public event EventHandler<ProjectSummary>? ProjectChosen;

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (Set(ref _searchQuery, value ?? "")) ApplyFilter();
        }
    }

    public int TotalCount => _all.Count;

    public bool HasAnyProjects => _all.Count > 0;

    /// <summary>True when a search is active but matched nothing — a distinct empty state.</summary>
    public bool HasNoMatches => _all.Count > 0 && VisibleProjects.Count == 0;

    public string CountSummary => _all.Count == 1
        ? "1 research project"
        : $"{_all.Count} research projects";

    public void Load(IReadOnlyList<ProjectSummary> projects)
    {
        _all = projects ?? Array.Empty<ProjectSummary>();
        _searchQuery = "";
        OnPropertyChanged(nameof(SearchQuery));
        ApplyFilter();

        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(HasAnyProjects));
        OnPropertyChanged(nameof(CountSummary));
    }

    public void Choose(ProjectSummary project)
    {
        if (project is null) return;
        ProjectChosen?.Invoke(this, project);
    }

    private void ApplyFilter()
    {
        VisibleProjects.Clear();

        foreach (var p in _all)
        {
            if (p.MatchesSearch(_searchQuery)) VisibleProjects.Add(p);
        }

        OnPropertyChanged(nameof(HasNoMatches));
    }
}
