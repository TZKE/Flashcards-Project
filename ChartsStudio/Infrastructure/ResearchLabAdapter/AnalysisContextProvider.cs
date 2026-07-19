using System.Text;
using AIFlashcardMaker.ChartsStudio.Domain.Context;
using AIFlashcardMaker.ChartsStudio.Domain.Projects;
using AIFlashcardMaker.ChartsStudio.Infrastructure.Persistence;

namespace AIFlashcardMaker.ChartsStudio.Infrastructure.ResearchLabAdapter;

/// <summary>
/// Charts Studio Phase 1 — THE BOUNDARY.
///
/// This is the ONLY type in the entire Charts Studio module permitted to reference Research Lab
/// types (ResearchProject, ResearchVariable, SavedComputedResult, CsvSampleSummary). Everything
/// else in the module works exclusively with the projected Domain.Context types.
///
/// If that rule is ever broken, the module's central guarantee goes with it: that Charts Studio
/// cannot disagree with Research Lab about what the data means, because it never forms its own
/// opinion. When Research Lab's internals change, exactly this file should need attention.
///
/// WHAT THIS TYPE DOES NOT DO
///   • It never reads a CSV or touches a data row.
///   • It never computes a statistic.
///   • It never re-classifies a variable using observed data.
///
/// The type/level to kind mapping below MIRRORS ResearchLabStatistics.Prepare exactly, whose
/// own comment states the principle: "Kind from the SHEET (source of truth), never from the
/// CSV." Mirroring rather than calling keeps the module boundary clean; the mapping is small,
/// stable, and covered by the mirrored-rule note on each branch. If Prepare's rules change,
/// this must change with them — that coupling is deliberate and documented rather than hidden.
/// </summary>
public sealed class AnalysisContextProvider
{
    private readonly Func<IEnumerable<ResearchProject>> _projectSource;

    /// <param name="projectSource">
    /// Supplies the host's current research projects.
    ///
    /// Taking the source HERE rather than at the session or view model is what makes this file
    /// the genuine sole contact point. If the session accepted a ResearchProject, the Research
    /// Lab type would appear in the Application layer, and from there in Presentation — and the
    /// boundary would exist only in the documentation. Layers above this one deal exclusively
    /// in project ids and projected Domain.Context types.
    ///
    /// A callback rather than a stored list so the picker always reflects projects created
    /// since the studio was last opened, without Charts Studio owning or mutating host state.
    /// </param>
    public AnalysisContextProvider(Func<IEnumerable<ResearchProject>> projectSource)
    {
        _projectSource = projectSource ?? throw new ArgumentNullException(nameof(projectSource));
    }

    /// <summary>
    /// Builds the lightweight picker summaries for every project. Deliberately cheap: no
    /// context is assembled, because most of these projects will not be opened.
    /// </summary>
    /// <param name="states">
    /// Charts Studio's per-project sidecar states, keyed by project id — supplies the figure
    /// count. Projects with no sidecar simply have none yet.
    /// </param>
    /// <param name="lastOpenedProjectId">
    /// Derived by the store from the sidecars rather than read from a global index.
    /// </param>
    public IReadOnlyList<ProjectSummary> BuildSummaries(
        IReadOnlyDictionary<string, ChartsStudioProjectState> states,
        string? lastOpenedProjectId)
    {
        var result = new List<ProjectSummary>();

        foreach (var p in _projectSource())
        {
            if (p is null) continue;

            states.TryGetValue(p.Id, out var record);

            result.Add(new ProjectSummary
            {
                Id = p.Id,
                Title = p.Title ?? "",
                Specialty = p.Specialty ?? "",
                StudyType = p.StudyType ?? "",
                UpdatedAt = ToLocal(p.UpdatedAt),
                VariableCount = p.Variables?.Count ?? 0,
                ParticipantCount = ParticipantCountOf(p),
                ResultCount = p.ComputedResults?.Count ?? 0,
                FigureCount = record?.FigureCount ?? 0,
                IsLastOpened = string.Equals(lastOpenedProjectId, p.Id, StringComparison.Ordinal)
            });
        }

        return result;
    }

    /// <summary>
    /// Assembles the full immutable snapshot for one project, identified by id.
    ///
    /// Returns null when no project with that id exists — which is a normal condition, not an
    /// error: a project can be deleted in Research Lab between the picker listing it and the
    /// user activating its card. Callers refresh rather than fail.
    /// </summary>
    public AnalysisContext? BuildContext(string projectId)
    {
        if (string.IsNullOrEmpty(projectId)) return null;

        var project = _projectSource()
            .FirstOrDefault(p => p is not null && string.Equals(p.Id, projectId, StringComparison.Ordinal));

        return project is null ? null : BuildContextFrom(project);
    }

    /// <summary>
    /// The projection itself. Separated from lookup so the mapping can be exercised directly
    /// against a constructed project without going through a source.
    /// </summary>
    public AnalysisContext BuildContextFrom(ResearchProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var variables = (project.Variables ?? new List<ResearchVariable>())
            .Where(v => v is not null)
            .Select(ProjectVariable)
            .ToList();

        var results = (project.ComputedResults ?? new List<SavedComputedResult>())
            .Where(r => r is not null)
            .Select(ProjectResult)
            .ToList();

        int? participants = ParticipantCountOf(project);

        var context = new AnalysisContext
        {
            ProjectId = project.Id,
            ProjectTitle = project.Title ?? "",
            CapturedAt = DateTime.Now,
            Variables = variables,
            Results = results,
            Design = ProjectDesign(project),
            ParticipantCount = participants,
            Readiness = EvaluateReadiness(variables, participants),
            Fingerprint = ComputeFingerprint(project, variables, results, participants)
        };

        return context;
    }

    // ---------------------------------------------------------------------------------
    // Projection
    // ---------------------------------------------------------------------------------

    private static ContextVariable ProjectVariable(ResearchVariable v)
    {
        string type = (v.VariableType ?? "").Trim();
        string level = (v.MeasurementLevel ?? "").Trim();
        string role = (v.Role ?? "").Trim();

        var kind = ResolveKind(type, level, role);

        return new ContextVariable
        {
            Id = v.Id ?? "",
            Name = v.VariableName ?? "",
            DisplayName = string.IsNullOrWhiteSpace(v.QuestionLabel)
                ? (v.VariableName ?? "")
                : v.QuestionLabel,
            DeclaredType = type,
            DeclaredLevel = level,
            DeclaredRole = role,
            Kind = kind,
            Role = ResolveRole(role),
            Units = "",                       // not modelled on the sheet yet
            IsObservedDataAvailable = false,  // no dataset is read in Phase 1
            ValidN = null,
            MissingN = null,
            ObservedCategoryCount = null
        };
    }

    /// <summary>
    /// Mirrors ResearchLabStatistics.Prepare's sheet-derived classification. Each branch below
    /// corresponds to one branch there; keep them in step.
    /// </summary>
    private static ContextVariableKind ResolveKind(string type, string level, string role)
    {
        // Mirrors Prepare: identifiers are excluded regardless of type.
        if (role.Equals("Identifier", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("ID", StringComparison.OrdinalIgnoreCase))
            return ContextVariableKind.Unsupported;

        // Mirrors Prepare: unset type is excluded — for charting that means "review first".
        if (type.Length == 0 || type.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            return ContextVariableKind.Ambiguous;

        if (type.Equals("Continuous", StringComparison.OrdinalIgnoreCase))
            return ContextVariableKind.Continuous;

        // Mirrors Prepare: numeric codes are categorical when the sheet marks the level as
        // Nominal/Ordinal; otherwise numeric means a measured quantity.
        if (type.Equals("Numeric", StringComparison.OrdinalIgnoreCase))
        {
            if (level.Equals("Ordinal", StringComparison.OrdinalIgnoreCase))
                return ContextVariableKind.Ordinal;
            if (level.Equals("Nominal", StringComparison.OrdinalIgnoreCase))
                return ContextVariableKind.Nominal;
            return ContextVariableKind.Continuous;
        }

        if (type.Equals("Binary", StringComparison.OrdinalIgnoreCase))
            return ContextVariableKind.Binary;

        // "Categorical" cannot be narrowed to Binary from the sheet alone — only observation
        // can confirm exactly two levels, and Phase 1 reads no data. Nominal is the honest
        // reading; a sheet that means binary should declare Binary.
        if (type.Equals("Categorical", StringComparison.OrdinalIgnoreCase))
            return ContextVariableKind.Nominal;

        if (type.Equals("Ordinal", StringComparison.OrdinalIgnoreCase))
            return ContextVariableKind.Ordinal;

        // Mirrors Prepare's final branch: Text, Date, anything else.
        return ContextVariableKind.Unsupported;
    }

    /// <summary>
    /// Mirrors ResearchLabTestRecommendations.ClassifyRole, including its exact vocabulary, so
    /// the two modules agree on what a role means.
    /// </summary>
    private static ContextVariableRole ResolveRole(string role)
    {
        string r = role.Trim().ToLowerInvariant();

        if (r is "identifier" or "id" or "metadata")
            return ContextVariableRole.Excluded;

        if (r is "outcome" or "secondary outcome" or "dependent variable" or "dependent")
            return ContextVariableRole.Outcome;

        if (r is "predictor" or "exposure" or "group" or "independent variable" or "independent"
              or "risk factor" or "confounder" or "covariate")
            return ContextVariableRole.Predictor;

        return ContextVariableRole.Unclear;
    }

    private static ContextResult ProjectResult(SavedComputedResult r) => new()
    {
        Id = r.Id ?? "",
        TestName = r.TestName ?? "",
        Variables = r.Variables ?? "",
        ValidNDisplay = r.ValidNDisplay ?? "",
        PValueDisplay = r.PValueDisplay ?? "",
        EffectDisplay = r.EffectDisplay ?? "",
        HasPValue = r.HasPValue,
        IsSignificant = r.IsSignificant,
        AnalysisFingerprint = r.AnalysisFingerprint ?? "",
        ComputedAt = r.ComputedAt
    };

    private static ContextDesign ProjectDesign(ResearchProject p)
    {
        var rec = p.Recommendations;

        return new ContextDesign
        {
            StudyType = p.StudyType ?? "",
            Specialty = p.Specialty ?? "",
            Aim = p.Aim ?? "",
            Population = p.Population ?? "",
            ResearchQuestion = rec?.ResearchQuestion ?? "",
            PrimaryObjective = rec?.PrimaryObjective ?? "",
            SecondaryObjectives = rec?.SecondaryObjectives?.ToArray() ?? Array.Empty<string>()
        };
    }

    /// <summary>
    /// Participants as the PROJECT reports them. Charts Studio never counts rows itself, and
    /// never falls back to a target sample size — a planned n is not an observed n, and
    /// conflating them would put a number on a figure that no data supports.
    /// </summary>
    private static int? ParticipantCountOf(ResearchProject p)
    {
        var csv = p.CsvSampleSummary;
        if (csv is null) return null;
        return csv.TotalRows > 0 ? csv.TotalRows : null;
    }

    // ---------------------------------------------------------------------------------
    // Readiness (foundation-level — see ReadinessSummary for scope)
    // ---------------------------------------------------------------------------------

    private static ReadinessSummary EvaluateReadiness(
        IReadOnlyList<ContextVariable> variables,
        int? participants)
    {
        var blockers = new List<string>();
        var warnings = new List<string>();

        if (variables.Count == 0)
            blockers.Add("This project has no variables yet. Build the Extraction Sheet first.");

        if (!participants.HasValue)
            blockers.Add("This project has no imported dataset yet.");

        if (blockers.Count > 0)
            return ReadinessSummary.Blocked(blockers.ToArray());

        int chartable = variables.Count(v => v.IsChartable);
        if (chartable == 0)
            return ReadinessSummary.Blocked(
                "No variable in this project can carry a figure — every one is an identifier, " +
                "free text, a date, or has no type set.");

        int ambiguous = variables.Count(v => v.Kind == ContextVariableKind.Ambiguous);
        if (ambiguous > 0)
            warnings.Add($"{ambiguous} variable{(ambiguous == 1 ? " has" : "s have")} no type set " +
                         "in the Extraction Sheet and cannot be charted until reviewed.");

        int unclearRole = variables.Count(v =>
            v.IsChartable && v.Role == ContextVariableRole.Unclear);
        if (unclearRole > 0)
            warnings.Add($"{unclearRole} variable{(unclearRole == 1 ? " has" : "s have")} an " +
                         "unclear role, which limits how figures can be ranked.");

        return warnings.Count > 0
            ? ReadinessSummary.NeedsReview(warnings.ToArray())
            : ReadinessSummary.Ready();
    }

    // ---------------------------------------------------------------------------------
    // Fingerprint
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Builds the canonical input string and hashes it.
    ///
    /// INCLUDED — anything that can change what a figure shows: the variable set and each
    /// variable's declared meaning, the dataset size, and the identity of every computed
    /// result.
    ///
    /// EXCLUDED — cosmetic project fields (title, notes, specialty). Renaming a project must
    /// not invalidate its figures.
    ///
    /// Ordering is fixed and formatting is culture-invariant so the same project state always
    /// produces the same fingerprint, on any machine, in any locale.
    /// </summary>
    private static ContextFingerprint ComputeFingerprint(
        ResearchProject project,
        IReadOnlyList<ContextVariable> variables,
        IReadOnlyList<ContextResult> results,
        int? participants)
    {
        var sb = new StringBuilder();

        sb.Append("v1\n");
        sb.Append("project:").Append(project.Id).Append('\n');
        sb.Append("rows:").Append(participants?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-").Append('\n');
        sb.Append("sheet:").Append(project.ExtractionSheetUpdatedAt?.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-").Append('\n');

        sb.Append("vars:\n");
        foreach (var v in variables.OrderBy(v => v.Id, StringComparer.Ordinal))
        {
            sb.Append("  ").Append(v.Id)
              .Append('|').Append(v.Name)
              .Append('|').Append(v.DeclaredType)
              .Append('|').Append(v.DeclaredLevel)
              .Append('|').Append(v.DeclaredRole)
              .Append('\n');
        }

        sb.Append("results:\n");
        foreach (var r in results.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            sb.Append("  ").Append(r.Id)
              .Append('|').Append(r.AnalysisFingerprint)
              .Append('\n');
        }

        return ContextFingerprint.FromCanonicalInput(sb.ToString());
    }

    private static DateTime ToLocal(DateTime utc) =>
        utc.Kind == DateTimeKind.Utc ? utc.ToLocalTime() : utc;
}
