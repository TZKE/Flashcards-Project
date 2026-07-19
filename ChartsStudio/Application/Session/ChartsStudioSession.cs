using AIFlashcardMaker.ChartsStudio.Domain.Context;
using AIFlashcardMaker.ChartsStudio.Domain.Projects;
using AIFlashcardMaker.ChartsStudio.Infrastructure.Persistence;
using AIFlashcardMaker.ChartsStudio.Infrastructure.ResearchLabAdapter;

namespace AIFlashcardMaker.ChartsStudio.Application.Session;

/// <summary>Where the studio currently is. Drives which surface the host shows.</summary>
public enum SessionPhase
{
    /// <summary>Nothing loaded yet.</summary>
    Idle,

    /// <summary>Multiple projects exist and the user is choosing one.</summary>
    Picking,

    /// <summary>A project is being prepared.</summary>
    Loading,

    /// <summary>A project is open and the studio shell is showing.</summary>
    Open,

    /// <summary>Loading failed; the reason is on <see cref="ChartsStudioSession.LoadError"/>.</summary>
    Failed
}

/// <summary>
/// Charts Studio Phase 1 — the session: the single owner of "what is open right now".
///
/// The session holds state and coordinates the store and the adapter. It deliberately owns no
/// rules of its own: what a variable means belongs to the adapter's projection, what is
/// persisted belongs to the store, and what a figure is will belong to the figure services.
/// Keeping orchestration free of rules is what lets each of those be tested on its own.
///
/// PHASE 1 SCOPE
/// The session prepares a project and stops. It does not generate recommendations, does not
/// render anything, and does not create figures — those arrive in later phases and hook onto
/// the extension points marked below.
/// </summary>
public sealed class ChartsStudioSession
{
    private readonly ChartsStudioStore _store;
    private readonly AnalysisContextProvider _provider;

    /// <summary>
    /// Cached snapshot of every project sidecar, refreshed on entry.
    ///
    /// Charts Studio keeps no global state file: the sidecars ARE the index. This is a read
    /// cache of them, not a second source of truth — every write goes straight to the owning
    /// project's file.
    /// </summary>
    private IReadOnlyDictionary<string, ChartsStudioProjectState> _states;

    public ChartsStudioSession(ChartsStudioStore store, AnalysisContextProvider provider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _states = _store.LoadAll();
    }

    // ---- Session state --------------------------------------------------------------

    /// <summary>Where the studio currently is.</summary>
    public SessionPhase Phase { get; private set; } = SessionPhase.Idle;

    /// <summary>The research project id currently open, or null.</summary>
    public string? CurrentProjectId { get; private set; }

    /// <summary>
    /// The immutable snapshot for the open project. <see cref="AnalysisContext.None"/> when
    /// nothing is open — never null, so callers never need a null check before reading counts.
    /// </summary>
    public AnalysisContext CurrentContext { get; private set; } = AnalysisContext.None;

    /// <summary>
    /// The project the user had open last, across app restarts. Derived from the sidecars
    /// rather than stored, so there is no index that can disagree with the files.
    /// </summary>
    public string? LastOpenedProjectId => ChartsStudioStore.ResolveLastOpenedProjectId(_states);

    /// <summary>
    /// Whether the session holds work that is not yet on disk.
    ///
    /// Always false in Phase 1: nothing here is user-authored yet. The flag exists now because
    /// every future surface that CAN dirty the session (editing a figure, reordering a
    /// collection) needs somewhere to say so, and retrofitting a dirty flag after several
    /// surfaces already mutate state is how unsaved-work bugs get introduced.
    /// </summary>
    public bool HasUnsavedChanges { get; private set; }

    /// <summary>Human-readable reason the last load failed, when <see cref="Phase"/> is Failed.</summary>
    public string? LoadError { get; private set; }

    /// <summary>Non-fatal note from the persistence layer, surfaced for diagnostics.</summary>
    public string? PersistenceNotice => _store.LastLoadIssue;

    /// <summary>
    /// EXTENSION POINT — the figure collection for the open project.
    ///
    /// Null throughout Phase 1 and never populated. Declared now so the first figure-bearing
    /// phase attaches to an existing session shape rather than reshaping the session, and so
    /// the persisted file already reserves the slot (see ChartsStudioProjectRecord.Figures).
    /// </summary>
    public object? FigureCollection { get; private set; }

    /// <summary>Raised whenever session state changes, so views can refresh.</summary>
    public event EventHandler? Changed;

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    // ---- Project listing ------------------------------------------------------------

    /// <summary>
    /// Builds picker summaries, ordered so the most useful project is first: last-opened, then
    /// most recently modified.
    /// </summary>
    public IReadOnlyList<ProjectSummary> ListProjects()
    {
        var summaries = _provider.BuildSummaries(_states, LastOpenedProjectId);

        return summaries
            .OrderByDescending(s => s.IsLastOpened)
            .ThenByDescending(s => s.UpdatedAt)
            .ToList();
    }

    // ---- Opening --------------------------------------------------------------------

    /// <summary>
    /// Whether entering the studio with this project list should skip the picker.
    ///
    /// Exactly one project opens straight into the shell — making a user pick from a list of
    /// one is pure friction. Zero or several show the picker (zero shows its empty state).
    /// </summary>
    public static bool ShouldAutoOpen(IReadOnlyList<ProjectSummary> summaries) =>
        summaries.Count == 1;

    /// <summary>Moves the session to the picker surface.</summary>
    public void ShowPicker()
    {
        Phase = SessionPhase.Picking;
        LoadError = null;
        RaiseChanged();
    }

    /// <summary>
    /// Prepares a project: projects the context, records the visit, and stops. No
    /// recommendations, no rendering — Phase 1 ends here by design.
    ///
    /// Takes an id rather than a project so no Research Lab type reaches this layer; the
    /// adapter owns the lookup.
    /// </summary>
    public bool Open(string projectId)
    {
        if (string.IsNullOrEmpty(projectId)) return false;

        Phase = SessionPhase.Loading;
        LoadError = null;
        RaiseChanged();

        try
        {
            var context = _provider.BuildContext(projectId);

            if (context is null)
            {
                CurrentProjectId = null;
                CurrentContext = AnalysisContext.None;
                LoadError = "That project no longer exists.";
                Phase = SessionPhase.Failed;
                RaiseChanged();
                return false;
            }

            CurrentProjectId = projectId;
            CurrentContext = context;
            HasUnsavedChanges = false;
            FigureCollection = null;   // populated by a later phase

            RememberVisit(projectId, context.Fingerprint.Value);

            Phase = SessionPhase.Open;
            RaiseChanged();
            return true;
        }
        catch (Exception ex)
        {
            CurrentProjectId = null;
            CurrentContext = AnalysisContext.None;
            LoadError = $"This project could not be prepared ({ex.GetType().Name}).";
            Phase = SessionPhase.Failed;
            RaiseChanged();
            return false;
        }
    }

    /// <summary>Returns to the picker without discarding what is remembered on disk.</summary>
    public void CloseProject()
    {
        CurrentProjectId = null;
        CurrentContext = AnalysisContext.None;
        FigureCollection = null;
        HasUnsavedChanges = false;
        LoadError = null;
        Phase = SessionPhase.Picking;
        RaiseChanged();
    }

    // ---- Persistence ----------------------------------------------------------------

    /// <summary>
    /// Records that this project was opened, and the fingerprint it was opened at.
    ///
    /// The fingerprint is stored from Phase 1 even though no figures exist yet, so that
    /// staleness works correctly for the very first figures ever saved rather than needing a
    /// retroactive backfill.
    ///
    /// A failed write is non-fatal: losing the "last opened" memory must never block the user.
    /// </summary>
    private void RememberVisit(string projectId, string fingerprint)
    {
        // Read-modify-write ONE project's sidecar. No other project's file is touched, and
        // there is no shared index to update — which is what makes this safe to do on every
        // open regardless of how many projects exist.
        var record = _store.Load(projectId);
        record.ProjectId = projectId;
        record.LastOpenedAt = DateTime.UtcNow;
        record.LastFingerprint = fingerprint;

        _store.Save(record);

        // Keep the in-memory view consistent with what was just written, so LastOpenedProjectId
        // is correct immediately rather than only after the next folder read.
        var refreshed = new Dictionary<string, ChartsStudioProjectState>(_states, StringComparer.Ordinal)
        {
            [projectId] = record
        };
        _states = refreshed;
    }

    /// <summary>
    /// Re-reads every project sidecar from disk. Used on entry, when another part of the app
    /// may have changed the underlying projects.
    /// </summary>
    public void ReloadPersistedState() => _states = _store.LoadAll();

    /// <summary>
    /// EXTENSION POINT — marks the session dirty. Unused in Phase 1; the first surface that
    /// mutates user-authored state calls this.
    /// </summary>
    public void MarkDirty()
    {
        if (HasUnsavedChanges) return;
        HasUnsavedChanges = true;
        RaiseChanged();
    }
}
