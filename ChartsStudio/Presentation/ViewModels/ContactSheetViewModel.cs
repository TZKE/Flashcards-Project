using System.Collections.ObjectModel;
using System.Windows.Threading;
using AIFlashcardMaker.ChartsStudio.Application.Rendering;
using AIFlashcardMaker.ChartsStudio.Application.Session;
using AIFlashcardMaker.ChartsStudio.Domain.Context;
using AIFlashcardMaker.ChartsStudio.Domain.Recommendation;
using AIFlashcardMaker.ChartsStudio.Domain.Specs;

namespace AIFlashcardMaker.ChartsStudio.Presentation.ViewModels;

/// <summary>
/// Charts Studio Phase 2 — the Contact Sheet.
///
/// Charts Studio does not ask what you would like to make. It shows the figures your project
/// already supports and asks which ones to keep. That inversion — curation rather than creation
/// — is the whole point of this surface, and it is why the sheet arrives populated rather than
/// as a blank canvas with a chart menu.
///
/// ORDER OF OPERATIONS, deliberately:
///   1. Recommend (deterministic, instant, offline) → cards exist with title and reason.
///   2. Render (async, bounded, cancellable) → thumbnails fill in.
/// The sheet is READABLE at the end of step 1. Nothing here waits on a network, and there is no
/// AI in this path at all.
/// </summary>
public sealed class ContactSheetViewModel : ObservableObject
{
    private readonly ChartsStudioSession _session;
    private readonly FigureRecommendationEngine _engine;
    private readonly FigureRenderQueue _renderQueue;
    private readonly Dispatcher _dispatcher;

    private AnalysisContext _context = AnalysisContext.None;
    private bool _isGenerating;
    private string _emptyReason = "";

    /// <summary>
    /// Thumbnail geometry.
    ///
    /// 16:9 to match the card's figure area exactly — a mismatched aspect letterboxes the
    /// figure and makes it look small inside its own card.
    ///
    /// Rendered at roughly 1.9× the displayed size with a matching ScaleFactor, which is the
    /// supersampling trick: fonts and line weights scale with the raster, so downscaling to the
    /// card yields crisp text at its intended size rather than soft or tiny text. The same
    /// ScaleFactor mechanism is what will carry a figure to 600 DPI at export.
    /// </summary>
    private const int ThumbWidth = 648;
    private const int ThumbHeight = 364;
    private const double ThumbScale = 1.9;

    public ContactSheetViewModel(
        ChartsStudioSession session,
        FigureRecommendationEngine engine,
        FigureRenderQueue renderQueue,
        Dispatcher dispatcher)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _renderQueue = renderQueue ?? throw new ArgumentNullException(nameof(renderQueue));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>The proposal cards, in ranked order.</summary>
    public ObservableCollection<FigureCardViewModel> Cards { get; } = new();

    /// <summary>True while proposals are being generated or drawn.</summary>
    public bool IsGenerating
    {
        get => _isGenerating;
        private set => Set(ref _isGenerating, value);
    }

    public bool HasCards => Cards.Count > 0;

    /// <summary>
    /// Why the sheet is empty, when it is. Never blank: an empty sheet with no explanation is
    /// the worst possible state, because the user cannot tell whether it is broken or working.
    /// </summary>
    public string EmptyReason
    {
        get => _emptyReason;
        private set => Set(ref _emptyReason, value);
    }

    public bool IsEmpty => !HasCards && !IsGenerating;

    public int KeptCount => _session.KeptFigureCount;

    public string KeptSummary => KeptCount switch
    {
        0 => "No figures kept yet",
        1 => "1 figure kept",
        _ => $"{KeptCount} figures kept"
    };

    /// <summary>Raised when the user asks to add a figure beyond the proposals.</summary>
    public event EventHandler? AddFigureRequested;

    /// <summary>
    /// Phase 3 — raised when the user asks to edit a card's figure. Editing implies keeping:
    /// an edit is an investment in the figure, and a patch needs a kept record to live on. The
    /// root view model performs the keep and opens the editor.
    /// </summary>
    public event EventHandler<FigureCardViewModel>? EditRequested;

    public void RequestEdit(FigureCardViewModel card)
    {
        if (card is null) return;

        // Keep first (no-op if already kept), so the editor always operates on a kept figure.
        if (!card.IsKept) ToggleKeep(card);
        EditRequested?.Invoke(this, card);
    }

    // ---------------------------------------------------------------------------------
    // Generation
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Builds and renders the sheet for a project. Safe to call repeatedly: any in-flight
    /// renders from a previous call are abandoned first, so a figure from the old project can
    /// never land on the new sheet.
    /// </summary>
    public async Task GenerateAsync(AnalysisContext context)
    {
        _context = context ?? AnalysisContext.None;

        _renderQueue.CancelAll();
        Cards.Clear();
        RaiseCollectionDerived();

        if (!_context.IsLoaded)
        {
            EmptyReason = "No project is open.";
            RaiseCollectionDerived();
            return;
        }

        IsGenerating = true;

        try
        {
            // Step 1 — deterministic, instant. Cards become readable here.
            var candidates = _engine.Recommend(_context);

            if (candidates.Count == 0)
            {
                EmptyReason = ComposeEmptyReason(_context);
                return;
            }

            foreach (var candidate in candidates)
            {
                var card = new FigureCardViewModel(candidate)
                {
                    IsKept = _session.IsKept(candidate.Spec)
                };
                Cards.Add(card);
            }

            RaiseCollectionDerived();

            // Step 2 — async rendering. Sequential await keeps request order matching card
            // order, while the queue's own bounded concurrency does the actual throttling.
            foreach (var card in Cards.ToList())
                await RenderCardAsync(card).ConfigureAwait(true);
        }
        finally
        {
            IsGenerating = false;
            RaiseCollectionDerived();
        }
    }

    private async Task RenderCardAsync(FigureCardViewModel card)
    {
        card.MarkRendering();

        var request = new RenderRequest
        {
            Spec = card.Spec,
            Context = _context,
            WidthPixels = ThumbWidth,
            HeightPixels = ThumbHeight,
            ScaleFactor = ThumbScale
        };

        var result = await _renderQueue.RenderAsync(request).ConfigureAwait(true);

        void Apply()
        {
            if (result.Succeeded && result.PngBytes is not null)
            {
                card.SetImage(result.PngBytes);
            }
            else if (string.IsNullOrEmpty(result.FailureReason))
            {
                // Superseded rather than failed — abandoned work is not an error to show.
                card.ResetToPending();
            }
            else
            {
                card.SetFailed(result.FailureReason);
            }
        }

        if (_dispatcher.CheckAccess()) Apply();
        else _dispatcher.Invoke(Apply);
    }

    /// <summary>
    /// Explains an empty sheet in terms the researcher can act on. The distinction that matters
    /// most: "no data yet" is a different problem from "nothing here can be charted".
    /// </summary>
    private static string ComposeEmptyReason(AnalysisContext context)
    {
        if (!context.HasDataset)
            return "This project has no imported dataset yet. Import your data in Research Lab, "
                 + "then run descriptive statistics.";

        bool anyObserved = context.Variables.Any(v => v.IsObservedDataAvailable);
        if (!anyObserved)
            return "Descriptive statistics have not been run for this project yet. Charts Studio "
                 + "draws figures from those results, so run them in Research Lab first.";

        if (context.ChartableVariableCount == 0)
            return "No variable in this project can carry a figure — every one is an identifier, "
                 + "free text, a date, or has no type set in the Extraction Sheet.";

        return "No figures could be proposed from this project's variables yet.";
    }

    // ---------------------------------------------------------------------------------
    // Actions
    // ---------------------------------------------------------------------------------

    /// <summary>Keeps a figure, or takes it off the shelf if it is already kept.</summary>
    public void ToggleKeep(FigureCardViewModel card)
    {
        if (card is null) return;

        if (card.IsKept)
        {
            var existing = _session.FindKeptByRenderKey(card.Spec);

            if (existing is not null) _session.RemoveFigure(existing.Id);
            card.IsKept = false;
        }
        else
        {
            _session.KeepFigure(card.Spec);
            card.IsKept = true;
        }

        OnPropertyChanged(nameof(KeptCount));
        OnPropertyChanged(nameof(KeptSummary));
    }

    /// <summary>
    /// Removes a proposal from the sheet.
    ///
    /// Removing a card the user had kept also takes it off the shelf: leaving a figure on the
    /// shelf after its card was dismissed would be a state the user cannot see or explain.
    /// </summary>
    public void RemoveCard(FigureCardViewModel card)
    {
        if (card is null) return;

        if (card.IsKept)
        {
            var existing = _session.FindKeptByRenderKey(card.Spec);
            if (existing is not null) _session.RemoveFigure(existing.Id);
        }

        Cards.Remove(card);
        RaiseCollectionDerived();

        if (Cards.Count == 0)
            EmptyReason = "You have removed every proposed figure. Add one to start again.";
    }

    /// <summary>Entry point for building a figure the recommender did not propose.</summary>
    public void RequestAddFigure() => AddFigureRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Adds a figure chosen from the Add Figure surface and renders it in place.
    /// </summary>
    public async Task AddCandidateAsync(FigureCandidate candidate)
    {
        if (candidate is null) return;

        var card = new FigureCardViewModel(candidate)
        {
            IsKept = _session.IsKept(candidate.Spec)
        };

        Cards.Add(card);
        EmptyReason = "";
        RaiseCollectionDerived();

        await RenderCardAsync(card).ConfigureAwait(true);
    }

    private void RaiseCollectionDerived()
    {
        OnPropertyChanged(nameof(HasCards));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(KeptCount));
        OnPropertyChanged(nameof(KeptSummary));
    }
}
