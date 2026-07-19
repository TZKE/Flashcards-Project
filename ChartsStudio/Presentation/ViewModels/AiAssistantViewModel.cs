using System.Collections.ObjectModel;
using System.Windows.Threading;
using AIFlashcardMaker.ChartsStudio.Application.Ai;
using AIFlashcardMaker.ChartsStudio.Application.Session;
using AIFlashcardMaker.ChartsStudio.Domain.Ai;
using AIFlashcardMaker.ChartsStudio.Domain.Context;
using AIFlashcardMaker.ChartsStudio.Domain.Specs;
using AIFlashcardMaker.ChartsStudio.Domain.Themes;

namespace AIFlashcardMaker.ChartsStudio.Presentation.ViewModels;

/// <summary>One advisory item as the panel shows it — severity colour + source label.</summary>
public sealed class AiAdvisoryItemViewModel
{
    public required AiAdvisoryItem Item { get; init; }
    public string Title => Item.Title;
    public string Detail => Item.Detail;
    public string SourceLabel => Item.Source == AiAdvisorySource.Deterministic ? "Automated check" : "AI suggestion";
    public string SeverityGlyph => Item.Severity switch
    {
        AiAdvisorySeverity.Warning => "!",
        AiAdvisorySeverity.Suggestion => "→",
        _ => "i"
    };
    public bool IsWarning => Item.Severity == AiAdvisorySeverity.Warning;
    public bool IsDeterministic => Item.Source == AiAdvisorySource.Deterministic;
}

/// <summary>
/// Charts Studio Phase 6 — the AI Advisory Assistant, "Publication Assistant" in the UI.
///
/// A thin shim over ChartsStudioAiService (Application), which owns every rule. This class marshals
/// results onto the dispatcher and exposes bindable state. Its guiding behaviour mirrors the
/// service's: the review actions are ALWAYS offered (they have a deterministic core and run
/// offline); AI is a bonus that arrives when available. A caption draft is shown for the user to
/// apply — never applied automatically.
/// </summary>
public sealed class AiAssistantViewModel : ObservableObject
{
    private readonly ChartsStudioAiService _service;
    private readonly ChartsStudioSession _session;
    private readonly Dispatcher _dispatcher;

    private bool _isOpen;
    private bool _isBusy;
    private string _headerText = "";
    private string _statusNote = "";
    private string _captionDraft = "";
    private bool _hasCaptionDraft;
    private CancellationTokenSource? _cts;

    // What the panel is currently attached to.
    private KeptFigure? _figure;
    private AnalysisContext _context = AnalysisContext.None;
    private int _figureIndex;

    public AiAssistantViewModel(ChartsStudioAiService service, ChartsStudioSession session, Dispatcher dispatcher)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>Raised when the user applies a caption draft to a figure (figureId, caption).</summary>
    public event EventHandler<(string FigureId, string Caption)>? CaptionApplied;

    public ObservableCollection<AiAdvisoryItemViewModel> Items { get; } = new();

    public bool IsOpen { get => _isOpen; private set => Set(ref _isOpen, value); }
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) RaiseActions(); } }
    public string HeaderText { get => _headerText; private set => Set(ref _headerText, value); }
    public string StatusNote { get => _statusNote; private set => Set(ref _statusNote, value); }

    public string CaptionDraft { get => _captionDraft; private set => Set(ref _captionDraft, value); }
    public bool HasCaptionDraft { get => _hasCaptionDraft; private set => Set(ref _hasCaptionDraft, value); }

    public bool HasItems => Items.Count > 0;
    public bool IsEmptyResult => !IsBusy && !HasItems && !HasCaptionDraft && StatusNote.Length > 0;

    /// <summary>Whether the model is reachable. The panel says so, but never gates the review
    /// actions on it — those work offline.</summary>
    public bool IsAiAvailable => _service.IsAiAvailable;

    public string AvailabilityNote => IsAiAvailable
        ? "AI is available. Reviews combine automated checks with AI suggestions."
        : "Sign in to your OrbitLab account for AI suggestions. Automated checks work offline.";

    // ---- Open / close ----------------------------------------------------------------

    /// <summary>Opens the assistant for a single figure (editor context).</summary>
    public void OpenForFigure(KeptFigure figure, AnalysisContext context, int figureIndex)
    {
        _figure = figure;
        _context = context ?? AnalysisContext.None;
        _figureIndex = figureIndex;
        HeaderText = "Publication assistant";
        Reset();
        IsOpen = true;
        OnPropertyChanged(nameof(IsAiAvailable));
        OnPropertyChanged(nameof(AvailabilityNote));
    }

    /// <summary>Opens the assistant for the whole set (shelf context) and runs the consistency
    /// review immediately, since that is the only set-level task.</summary>
    public void OpenForSet()
    {
        _figure = null;
        _context = _session.CurrentContext;
        HeaderText = "Review the figure set";
        Reset();
        IsOpen = true;
        OnPropertyChanged(nameof(IsAiAvailable));
        OnPropertyChanged(nameof(AvailabilityNote));
        _ = RunConsistencyAsync();
    }

    public void Close()
    {
        _cts?.Cancel();
        IsOpen = false;
    }

    public bool IsFigureContext => _figure is not null;

    // ---- Actions ---------------------------------------------------------------------

    public async Task DraftCaptionAsync()
    {
        if (_figure is null) return;
        await RunAsync(async ct =>
        {
            var figCtx = BuildFigureContext();
            var result = await _service.DraftCaptionAsync(figCtx, ct).ConfigureAwait(true);
            ApplyResult(result);
        });
    }

    public async Task CritiqueAsync()
    {
        if (_figure is null) return;
        await RunAsync(async ct =>
        {
            var (figCtx, style) = BuildFigureContextAndStyle();
            var result = await _service.CritiqueAsync(figCtx, _figure.Spec, style, ct).ConfigureAwait(true);
            ApplyResult(result);
        });
    }

    public async Task AccessibilityAsync()
    {
        if (_figure is null) return;
        await RunAsync(async ct =>
        {
            var (figCtx, style) = BuildFigureContextAndStyle();
            var result = await _service.ReviewAccessibilityAsync(figCtx, _figure.Spec, style, ct).ConfigureAwait(true);
            ApplyResult(result);
        });
    }

    private async Task RunConsistencyAsync() => await RunAsync(async ct =>
    {
        var figures = _session.KeptFigures;
        var withStyles = figures.Select((f, i) =>
            new FigureWithStyle(i + 1, f.Spec, FigureStyleResolver.Resolve(f.Spec, f.Patch))).ToList();

        var set = new AiFigureSetContext
        {
            Figures = figures.Select((f, i) => AiFigureContext.Build(
                i + 1, f.Spec, withStyles[i].Style, FindVariable(f.Spec), f.IsModified)).ToList(),
            StudyTitle = _context.ProjectTitle
        };

        var result = await _service.ReviewConsistencyAsync(set, withStyles, ct).ConfigureAwait(true);
        ApplyResult(result);
    });

    /// <summary>Applies the current caption draft to the figure. The one place AI output touches
    /// a figure — and only because the user pressed the button.</summary>
    public void ApplyCaption()
    {
        if (_figure is null || string.IsNullOrWhiteSpace(CaptionDraft)) return;
        CaptionApplied?.Invoke(this, (_figure.Id, CaptionDraft.Trim()));
        StatusNote = "Caption applied to the figure.";
        HasCaptionDraft = false;
    }

    // ---- Internals -------------------------------------------------------------------

    private async Task RunAsync(Func<CancellationToken, Task> body)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        Reset();
        IsBusy = true;
        try { await body(_cts.Token); }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex) { StatusNote = $"Something went wrong ({ex.GetType().Name})."; }
        finally { IsBusy = false; RaiseDerived(); }
    }

    private void ApplyResult(AiAdvisoryResult result)
    {
        void Apply()
        {
            Items.Clear();
            foreach (var item in result.Items)
                Items.Add(new AiAdvisoryItemViewModel { Item = item });

            CaptionDraft = result.DraftText;
            HasCaptionDraft = result.HasDraft;

            StatusNote = result.DegradationNote.Length > 0
                ? result.DegradationNote
                : result.Task switch
                {
                    AiAdvisoryTask.Caption => result.UsedAi ? "AI-written caption draft — review and apply." : "",
                    _ when result.Items.Count == 0 => "No issues found.",
                    _ => result.UsedAi ? "Automated checks and AI suggestions." : "Automated checks."
                };

            RaiseDerived();
        }

        if (_dispatcher.CheckAccess()) Apply();
        else _dispatcher.Invoke(Apply);
    }

    private void Reset()
    {
        Items.Clear();
        CaptionDraft = "";
        HasCaptionDraft = false;
        StatusNote = "";
        RaiseDerived();
    }

    private AiFigureContext BuildFigureContext() => BuildFigureContextAndStyle().Item1;

    private (AiFigureContext, ResolvedFigureStyle) BuildFigureContextAndStyle()
    {
        var style = FigureStyleResolver.Resolve(_figure!.Spec, _figure.Patch);
        var ctx = AiFigureContext.Build(_figureIndex, _figure.Spec, style, FindVariable(_figure.Spec), _figure.IsModified);
        return (ctx, style);
    }

    private ContextVariable? FindVariable(FigureSpec spec) =>
        _context.Variables.FirstOrDefault(v =>
            string.Equals(v.Id, spec.PrimaryVariableId, StringComparison.Ordinal));

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmptyResult));
    }

    private void RaiseActions() => OnPropertyChanged(nameof(IsEmptyResult));
}
