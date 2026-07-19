using AIFlashcardMaker.ChartsStudio.Application.Session;
using AIFlashcardMaker.ChartsStudio.Domain.Projects;

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

    private SessionPhase _phase = SessionPhase.Idle;

    /// <param name="session">The session this view model drives.</param>
    public ChartsStudioViewModel(ChartsStudioSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));

        Picker.ProjectChosen += (_, summary) => OpenProject(summary.Id);
        Shell.ChangeProjectRequested += (_, _) => ChangeProject();
        _session.Changed += (_, _) => SyncFromSession();
    }

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
        Phase = _session.Phase;

        if (_session.Phase == SessionPhase.Open)
            Shell.Load(_session.CurrentContext);

        OnPropertyChanged(nameof(ErrorMessage));
    }
}
