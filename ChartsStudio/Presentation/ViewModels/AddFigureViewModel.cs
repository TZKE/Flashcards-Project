using System.Collections.ObjectModel;
using AIFlashcardMaker.ChartsStudio.Domain.Context;
using AIFlashcardMaker.ChartsStudio.Domain.Recommendation;

namespace AIFlashcardMaker.ChartsStudio.Presentation.ViewModels;

/// <summary>
/// One choosable figure in the Add Figure list, with the reason it is or is not available.
/// </summary>
public sealed class AddFigureOption
{
    public required FigureCandidate Candidate { get; init; }

    public string Headline => Candidate.HeadlineDisplay;
    public string ChartTypeName => Candidate.ChartType.DisplayName;
    public string VariableName => Candidate.Variable.DisplayName;
    public string BestFor => Candidate.ChartType.BestFor;

    /// <summary>True when this exact figure is already on the contact sheet.</summary>
    public bool AlreadyOnSheet { get; init; }

    public string StatusLabel => AlreadyOnSheet ? "Already on the sheet" : "";
}

/// <summary>
/// Charts Studio Phase 2 — the Add Figure entry point.
///
/// The contact sheet leads with what the project needs; this is where a user goes when they
/// want something the recommender did not propose, or want to look at a specific variable.
///
/// It is deliberately NOT a chart-type menu. The list is organised BY VARIABLE, because a
/// researcher thinks "I need a figure for my primary outcome", not "I need a box plot". The
/// chart form is shown as supporting detail — the same inversion the cards use.
///
/// Only figures that can honestly be drawn appear. A variable with no descriptive statistics
/// contributes nothing, and the empty state says why rather than showing forms that would fail.
/// </summary>
public sealed class AddFigureViewModel : ObservableObject
{
    private readonly FigureRecommendationEngine _engine;
    private string _search = "";
    private AnalysisContext _context = AnalysisContext.None;
    private IReadOnlyList<AddFigureOption> _all = Array.Empty<AddFigureOption>();
    private bool _isOpen;

    public AddFigureViewModel(FigureRecommendationEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public ObservableCollection<AddFigureOption> Options { get; } = new();

    /// <summary>Raised when the user picks a figure to add.</summary>
    public event EventHandler<FigureCandidate>? OptionChosen;

    public bool IsOpen
    {
        get => _isOpen;
        private set => Set(ref _isOpen, value);
    }

    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value ?? "")) ApplyFilter(); }
    }

    public bool HasOptions => Options.Count > 0;

    public string EmptyReason { get; private set; } = "";

    /// <summary>
    /// Builds the list for the current project, marking figures already on the sheet so the
    /// user is not offered a duplicate without knowing it.
    /// </summary>
    public void Open(AnalysisContext context, IEnumerable<string> renderKeysOnSheet)
    {
        _context = context ?? AnalysisContext.None;
        var onSheet = new HashSet<string>(renderKeysOnSheet ?? Array.Empty<string>(), StringComparer.Ordinal);

        var options = new List<AddFigureOption>();

        foreach (var variable in _context.Variables)
        {
            if (!variable.IsChartable) continue;

            foreach (var candidate in _engine.RecommendForVariable(_context, variable.Id))
            {
                options.Add(new AddFigureOption
                {
                    Candidate = candidate,
                    AlreadyOnSheet = onSheet.Contains(candidate.Spec.ToRenderKey())
                });
            }
        }

        _all = options
            .OrderBy(o => o.VariableName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(o => o.Candidate.Score)
            .ToList();

        EmptyReason = _all.Count == 0
            ? "No figures can be drawn from this project yet. Run descriptive statistics in "
            + "Research Lab, then come back."
            : "";

        _search = "";
        OnPropertyChanged(nameof(Search));
        ApplyFilter();

        IsOpen = true;
    }

    public void Close() => IsOpen = false;

    public void Choose(AddFigureOption option)
    {
        if (option is null) return;
        OptionChosen?.Invoke(this, option.Candidate);
        IsOpen = false;
    }

    private void ApplyFilter()
    {
        Options.Clear();

        foreach (var o in _all)
        {
            bool match = string.IsNullOrWhiteSpace(_search)
                      || o.VariableName.Contains(_search, StringComparison.OrdinalIgnoreCase)
                      || o.ChartTypeName.Contains(_search, StringComparison.OrdinalIgnoreCase)
                      || o.Headline.Contains(_search, StringComparison.OrdinalIgnoreCase);

            if (match) Options.Add(o);
        }

        OnPropertyChanged(nameof(HasOptions));
        OnPropertyChanged(nameof(EmptyReason));
    }
}
