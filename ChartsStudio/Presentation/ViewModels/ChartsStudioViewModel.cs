using AIFlashcardMaker.ChartsStudio.Application.Session;
using AIFlashcardMaker.ChartsStudio.Application.Rendering;
using AIFlashcardMaker.ChartsStudio.Domain.Projects;
using AIFlashcardMaker.ChartsStudio.Domain.Recommendation;

namespace AIFlashcardMaker.ChartsStudio.Presentation.ViewModels;

/// <summary>
/// Charts Studio Phase 1 — the module's root view model.
///
/// This is the ONE object MainWindow talks to. Everything else in Charts Studio hangs beneath
/// it, which is what keeps the module's footprint in the host down to a single field and a
/// single entry call.
///
/// It owns which surface is showing (picker or shell) and translates session phase into the
/// booleans the host view binds its visibility to.
/// </summary>
public sealed class ChartsStudioViewModel : ObservableObject
{
    private readonly ChartsStudioSession _session;
    private readonly FigureRenderQueue _renderQueue;

    private SessionPhase _phase = SessionPhase.Idle;

    /// <param name="session">The session this view model drives.</param>
    /// <param name="renderQueue">
    /// Owned here rather than by the contact sheet, so switching projects can abandon every
    /// in-flight render in one call regardless of which surface is showing.
    /// </param>
    /// <param name="dispatcher">UI dispatcher, for marshalling completed renders back.</param>
    public ChartsStudioViewModel(
        ChartsStudioSession session,
        FigureRenderQueue renderQueue,
        System.Windows.Threading.Dispatcher dispatcher)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _renderQueue = renderQueue ?? throw new ArgumentNullException(nameof(renderQueue));

        var engine = new FigureRecommendationEngine();

        ContactSheet = new ContactSheetViewModel(_session, engine, _renderQueue, dispatcher);
        AddFigure = new AddFigureViewModel(engine);
        Shelf = new FigureShelfViewModel(_session, _renderQueue, dispatcher);
        Editor = new FigureEditorViewModel(_session, _renderQueue, dispatcher);

        Shell.AttachSurfaces(ContactSheet, AddFigure, Shelf, Editor);

        ContactSheet.AddFigureRequested += (_, _) => OpenAddFigure();
        AddFigure.OptionChosen += async (_, candidate) => await ContactSheet.AddCandidateAsync(candidate);

        // Phase 3/4 — edit paths. Both roads lead to the same editor over the same kept
        // figure; the contact sheet keeps first (see RequestEdit), the shelf edits directly.
        ContactSheet.EditRequested += (_, card) =>
        {
            var kept = _session.FindKeptByRenderKey(card.Spec);
            if (kept is not null) Editor.Open(kept, _session.CurrentContext);
        };
        Shelf.EditRequested += (_, item) =>
        {
            var kept = _session.FindKeptFigure(item.Id);
            if (kept is not null) Editor.Open(kept, _session.CurrentContext);
        };
        Editor.Saved += (_, _) => Shelf.Refresh();

        Picker.ProjectChosen += (_, summary) => OpenProject(summary.Id);
        Shell.ChangeProjectRequested += (_, _) => ChangeProject();
        _session.Changed += (_, _) => SyncFromSession();
    }

    public ContactSheetViewModel ContactSheet { get; }

    public AddFigureViewModel AddFigure { get; }

    public FigureShelfViewModel Shelf { get; }

    public FigureEditorViewModel Editor { get; }

    private void OpenAddFigure() =>
        AddFigure.Open(_session.CurrentContext, ContactSheet.Cards.Select(c => c.Spec.ToRenderKey()));

    public ProjectPickerViewModel Picker { get; } = new();

    public StudioShellViewModel Shell { get; } = new();

    // ---- Surface visibility ---------------------------------------------------------

    public SessionPhase Phase
    {
        get => _phase;
        private set
        {
            if (!Set(ref _phase, value)) return;
            OnPropertyChanged(nameof(IsPickerVisible));
            OnPropertyChanged(nameof(IsShellVisible));
            OnPropertyChanged(nameof(IsLoadingVisible));
            OnPropertyChanged(nameof(IsErrorVisible));
        }
    }

    public bool IsPickerVisible => Phase == SessionPhase.Picking;
    public bool IsShellVisible => Phase == SessionPhase.Open;
    public bool IsLoadingVisible => Phase == SessionPhase.Loading;
    public bool IsErrorVisible => Phase == SessionPhase.Failed;

    public string ErrorMessage => _session.LoadError ?? "";

    /// <summary>True when the user has more than one project, so "Change project" is meaningful.</summary>
    public bool CanChangeProject => Picker.TotalCount > 1;

    // ---- Entry ----------------------------------------------------------------------

    /// <summary>
    /// Called every time the user navigates to Charts Studio.
    ///
    /// Re-reads the project list on each entry rather than caching it, because projects can be
    /// created, renamed or deleted in Research Lab between visits and a stale picker would be
    /// worse than a marginally slower one.
    /// </summary>
    public void Enter()
    {
        _session.ReloadPersistedState();

        var summaries = _session.ListProjects();
        Picker.Load(summaries);
        OnPropertyChanged(nameof(CanChangeProject));

        // Already inside a project? Stay there rather than throwing the user back to the picker.
        if (_session.Phase == SessionPhase.Open && _session.CurrentProjectId is not null)
        {
            SyncFromSession();
            return;
        }

        if (ChartsStudioSession.ShouldAutoOpen(summaries))
        {
            OpenProject(summaries[0].Id);
            return;
        }

        _session.ShowPicker();
    }

    /// <summary>
    /// Prepares a project and moves to the shell. A project that vanished between listing and
    /// activation surfaces through the session's Failed phase rather than a hard error.
    /// </summary>
    public void OpenProject(string projectId) => _session.Open(projectId);

    /// <summary>Returns to the picker from an open project, refreshing the list on the way.</summary>
    public void ChangeProject()
    {
        Picker.Load(_session.ListProjects());
        OnPropertyChanged(nameof(CanChangeProject));

        _session.CloseProject();
    }

    private void SyncFromSession()
    {
        bool projectChanged = !string.Equals(_shownProjectId, _session.CurrentProjectId, StringComparison.Ordinal);

        Phase = _session.Phase;

        if (_session.Phase == SessionPhase.Open)
        {
            Shell.Load(_session.CurrentContext);

            // Regenerate only when the project actually changed. The session also raises
            // Changed on every keep and remove, and rebuilding the whole sheet on each of those
            // would throw away renders the user is looking at.
            if (projectChanged)
            {
                _shownProjectId = _session.CurrentProjectId;
                Shell.ShowSheet();
                _ = ContactSheet.GenerateAsync(_session.CurrentContext);
                Shelf.Refresh(_session.CurrentContext);
            }
            else
            {
                // Keep/remove/patch/reorder all land here: the shelf mirrors the session
                // cheaply (cache makes unchanged thumbnails instant), the sheet is untouched.
                Shelf.Refresh(_session.CurrentContext);
            }
        }
        else if (_session.Phase != SessionPhase.Loading)
        {
            _shownProjectId = null;
            _renderQueue.CancelAll();
        }

        OnPropertyChanged(nameof(ErrorMessage));
    }

    /// <summary>Which project the contact sheet currently shows, so it regenerates only on a real change.</summary>
    private string? _shownProjectId;
}
