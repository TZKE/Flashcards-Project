using AIFlashcardMaker.ChartsStudio.Domain.Context;

namespace AIFlashcardMaker.ChartsStudio.Presentation.ViewModels;

/// <summary>
/// Charts Studio Phase 1 — the studio shell for an open project.
///
/// Phase 1 builds the LAYOUT only: the three surfaces (Contact Sheet, Figure Canvas, Figure
/// Shelf) exist as placeholders, and navigation between them is not wired. What is real here
/// is the context header — the line that proves the project was actually understood before any
/// figure is proposed. That header is the first beat of the studio's opening sequence and the
/// one part of the shell worth getting right now, because everything later renders beneath it.
/// </summary>
public sealed class StudioShellViewModel : ObservableObject
{
    private AnalysisContext _context = AnalysisContext.None;

    public AnalysisContext Context
    {
        get => _context;
        private set
        {
            if (!Set(ref _context, value)) return;
            RaiseAllDerived();
        }
    }

    public void Load(AnalysisContext context) => Context = context ?? AnalysisContext.None;

    /// <summary>
    /// Raised when the user asks to go back to the picker. The root view model owns that
    /// transition; the shell only reports the intent — same pattern as the picker's
    /// ProjectChosen, so both surfaces stay unaware of each other.
    /// </summary>
    public event EventHandler? ChangeProjectRequested;

    public void RequestChangeProject() => ChangeProjectRequested?.Invoke(this, EventArgs.Empty);

    // ---- Header ---------------------------------------------------------------------

    public string ProjectTitle => string.IsNullOrWhiteSpace(Context.ProjectTitle)
        ? "Untitled project"
        : Context.ProjectTitle;

    /// <summary>
    /// The comprehension line: variables, participants, analyses. Phrased as counts the
    /// researcher recognises from their own project rather than as system state.
    /// </summary>
    public string ContextSummary
    {
        get
        {
            if (!Context.IsLoaded) return "No project loaded.";

            var parts = new List<string>(3)
            {
                $"{Context.VariableCount} variable{(Context.VariableCount == 1 ? "" : "s")}"
            };

            parts.Add(Context.ParticipantCount.HasValue
                ? $"{Context.ParticipantCount.Value} participant{(Context.ParticipantCount.Value == 1 ? "" : "s")}"
                : "no dataset");

            parts.Add(Context.ResultCount == 1 ? "1 analysis" : $"{Context.ResultCount} analyses");

            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>The variable breakdown, shown as secondary detail under the summary.</summary>
    public string VariableBreakdown
    {
        get
        {
            if (!Context.IsLoaded) return "";

            var parts = new List<string>(3);
            if (Context.ContinuousCount > 0) parts.Add($"{Context.ContinuousCount} continuous");
            if (Context.CategoricalCount > 0) parts.Add($"{Context.CategoricalCount} categorical");
            if (Context.ExcludedCount > 0) parts.Add($"{Context.ExcludedCount} not chartable");

            return parts.Count == 0 ? "" : string.Join("  ·  ", parts);
        }
    }

    public bool HasVariableBreakdown => VariableBreakdown.Length > 0;

    // ---- Readiness ------------------------------------------------------------------

    public string ReadinessDisplay => Context.Readiness.StateDisplay;

    public bool IsReady => Context.Readiness.State == ContextReadinessState.Ready;

    public bool NeedsReview => Context.Readiness.State == ContextReadinessState.NeedsReview;

    public bool IsBlocked => Context.Readiness.State == ContextReadinessState.Blocked;

    public bool HasReadinessReasons => Context.Readiness.Reasons.Count > 0;

    public string ReadinessReasons => string.Join("\n", Context.Readiness.Reasons);

    // ---- Provenance -----------------------------------------------------------------

    /// <summary>
    /// Short fingerprint, shown quietly in the shell. It looks like a developer detail and is
    /// not — it is the first visible thread of the provenance story, and having it on screen
    /// from Phase 1 means the concept is never bolted on later.
    /// </summary>
    public string FingerprintDisplay => Context.Fingerprint.ShortValue;

    public string CapturedAtDisplay => Context.IsLoaded
        ? Context.CapturedAt.ToString("d MMM yyyy · HH:mm")
        : "—";

    private void RaiseAllDerived()
    {
        OnPropertyChanged(nameof(ProjectTitle));
        OnPropertyChanged(nameof(ContextSummary));
        OnPropertyChanged(nameof(VariableBreakdown));
        OnPropertyChanged(nameof(HasVariableBreakdown));
        OnPropertyChanged(nameof(ReadinessDisplay));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(NeedsReview));
        OnPropertyChanged(nameof(IsBlocked));
        OnPropertyChanged(nameof(HasReadinessReasons));
        OnPropertyChanged(nameof(ReadinessReasons));
        OnPropertyChanged(nameof(FingerprintDisplay));
        OnPropertyChanged(nameof(CapturedAtDisplay));
    }
}
