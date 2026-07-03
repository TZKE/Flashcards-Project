using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace AIFlashcardMaker;

// ---------------------------------------------------------------------------
// Research Lab — in-app Research AI service layer (provider-neutral).
//
// SECURITY: there are NO provider API keys anywhere in this desktop app. The
// intended architecture is:
//
//     WPF app  ->  Research AI backend / proxy endpoint  ->  AI provider
//
// The app only talks to a configurable backend endpoint. Provider secrets live
// on that backend. From the user's point of view everything happens in-app:
// they click a button, wait for a loading state, and see results. No copy/paste
// and no provider names appear in the UI.
//
// A development-only mock is available (behind a settings toggle) so the UI and
// flow can be exercised before a real backend exists. It is clearly a draft and
// never claims to be real AI output.
// ---------------------------------------------------------------------------

public enum ResearchAiTaskType
{
    Recommendations,
    ProposalDraft
}

// Persisted to research_ai_config.json (separate from the Z.ai flashcard
// settings, which are never touched by the Research Lab).
public sealed class ResearchAiOptions
{
    public string EndpointBaseUrl { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 60;
    public bool UseDevelopmentMock { get; set; }

    [JsonIgnore]
    public bool IsConfigured =>
        UseDevelopmentMock || !string.IsNullOrWhiteSpace(EndpointBaseUrl);

    public ResearchAiOptions Clone() => new()
    {
        EndpointBaseUrl = EndpointBaseUrl,
        TimeoutSeconds = TimeoutSeconds,
        UseDevelopmentMock = UseDevelopmentMock
    };
}

// ---- Provider-neutral request/response DTOs -------------------------------
// Provider secrets never appear in these payloads.

public sealed class ResearchAiProjectPayload
{
    public string Title { get; set; } = "";
    public string Specialty { get; set; } = "";
    public string StudyType { get; set; } = "";
    public string Aim { get; set; } = "";
    public string Population { get; set; } = "";
    public string Setting { get; set; } = "";
    public string TimePeriod { get; set; } = "";
    public string AvailableDataType { get; set; } = "";
    public List<string> DesiredOutputs { get; set; } = new();
    public string Notes { get; set; } = "";

    public static ResearchAiProjectPayload From(ResearchProject p) => new()
    {
        Title = p.Title,
        Specialty = p.Specialty,
        StudyType = p.StudyType,
        Aim = p.Aim,
        Population = p.Population,
        Setting = p.Setting,
        TimePeriod = p.TimePeriod,
        AvailableDataType = p.AvailableDataType,
        DesiredOutputs = new List<string>(p.DesiredOutputs),
        Notes = p.Notes
    };
}

public sealed class ResearchAiRequest
{
    public string ProjectId { get; set; } = "";
    public string TaskType { get; set; } = "";   // "recommendations" | "proposal"
    public ResearchAiProjectPayload Project { get; set; } = new();
    public ResearchRecommendations? AcceptedRecommendations { get; set; }
    public string DesiredOutputFormat { get; set; } = "json";
}

public sealed class ResearchAiResponse
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = "";
    public ResearchRecommendations? Recommendations { get; set; }
    public ResearchProposalDraft? ProposalDraft { get; set; }
    public string RawText { get; set; } = "";
    public string ProviderMetadata { get; set; } = "";   // never surfaced in the UI
}

// ---- Exceptions the UI translates into friendly states --------------------

public sealed class ResearchAiNotConfiguredException : Exception
{
    public ResearchAiNotConfiguredException()
        : base("Research AI service is not configured yet.") { }
}

public sealed class ResearchAiException : Exception
{
    public ResearchAiException(string message) : base(message) { }
}

// ---- Service abstraction --------------------------------------------------

public interface IResearchAiService
{
    bool IsConfigured { get; }
    Task<ResearchRecommendations> GenerateRecommendationsAsync(ResearchProject project, CancellationToken cancellationToken);
    Task<ResearchProposalDraft> GenerateProposalDraftAsync(ResearchProject project, ResearchRecommendations? recommendations, CancellationToken cancellationToken);
}

// Facade that reads live options and dispatches to the HTTP client or the
// development mock. It never contains provider keys.
public sealed class ResearchAiService : IResearchAiService
{
    private readonly Func<ResearchAiOptions> _options;
    private readonly MockResearchAiService _mock = new();
    private readonly ResearchAiHttpClient _http;

    public ResearchAiService(Func<ResearchAiOptions> optionsProvider)
    {
        _options = optionsProvider;
        _http = new ResearchAiHttpClient(optionsProvider);
    }

    public bool IsConfigured => _options().IsConfigured;

    public Task<ResearchRecommendations> GenerateRecommendationsAsync(ResearchProject project, CancellationToken cancellationToken)
    {
        var o = _options();
        if (!o.IsConfigured) throw new ResearchAiNotConfiguredException();
        return o.UseDevelopmentMock
            ? _mock.GenerateRecommendationsAsync(project, cancellationToken)
            : _http.GenerateRecommendationsAsync(project, cancellationToken);
    }

    public Task<ResearchProposalDraft> GenerateProposalDraftAsync(ResearchProject project, ResearchRecommendations? recommendations, CancellationToken cancellationToken)
    {
        var o = _options();
        if (!o.IsConfigured) throw new ResearchAiNotConfiguredException();
        return o.UseDevelopmentMock
            ? _mock.GenerateProposalDraftAsync(project, recommendations, cancellationToken)
            : _http.GenerateProposalDraftAsync(project, recommendations, cancellationToken);
    }
}

// ---- Backend/proxy HTTP client --------------------------------------------
// Calls a configurable endpoint. Assumes future routes:
//   POST {base}/api/research-ai/recommendations
//   POST {base}/api/research-ai/proposal
// No API keys are sent from the app; the backend owns provider secrets.
public sealed class ResearchAiHttpClient : IResearchAiService
{
    private static readonly HttpClient Http = new();
    private readonly Func<ResearchAiOptions> _options;
    private readonly ResearchRecommendationParser _parser = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ResearchAiHttpClient(Func<ResearchAiOptions> optionsProvider)
    {
        _options = optionsProvider;
    }

    public bool IsConfigured => _options().IsConfigured;

    public async Task<ResearchRecommendations> GenerateRecommendationsAsync(ResearchProject project, CancellationToken cancellationToken)
    {
        var resp = await PostAsync("recommendations", ResearchAiTaskType.Recommendations, project, null, cancellationToken);
        if (resp.Recommendations is null || !resp.Recommendations.HasStructuredContent)
            throw new ResearchAiException("The Research AI service did not return any recommendations.");

        resp.Recommendations.SourceMode = ResearchSourceMode.AiGenerated;
        return resp.Recommendations;
    }

    public async Task<ResearchProposalDraft> GenerateProposalDraftAsync(ResearchProject project, ResearchRecommendations? recommendations, CancellationToken cancellationToken)
    {
        var resp = await PostAsync("proposal", ResearchAiTaskType.ProposalDraft, project, recommendations, cancellationToken);
        if (resp.ProposalDraft is null)
            throw new ResearchAiException("The Research AI service did not return a proposal draft.");

        resp.ProposalDraft.SourceMode = ResearchSourceMode.AiGenerated;
        resp.ProposalDraft.IsTemplateGenerated = true;
        return resp.ProposalDraft;
    }

    private async Task<ResearchAiResponse> PostAsync(
        string path,
        ResearchAiTaskType task,
        ResearchProject project,
        ResearchRecommendations? recommendations,
        CancellationToken cancellationToken)
    {
        var o = _options();
        if (string.IsNullOrWhiteSpace(o.EndpointBaseUrl))
            throw new ResearchAiNotConfiguredException();

        string url = o.EndpointBaseUrl.TrimEnd('/') + "/api/research-ai/" + path;

        var request = new ResearchAiRequest
        {
            ProjectId = project.Id,
            TaskType = task == ResearchAiTaskType.Recommendations ? "recommendations" : "proposal",
            Project = ResearchAiProjectPayload.From(project),
            AcceptedRecommendations = recommendations,
            DesiredOutputFormat = "json"
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 5, 300)));

        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(request, Json), Encoding.UTF8, "application/json");
            using var httpResp = await Http.PostAsync(url, content, cts.Token);
            string body = await httpResp.Content.ReadAsStringAsync(cts.Token);

            if (!httpResp.IsSuccessStatusCode)
                throw new ResearchAiException($"The Research AI service returned an error ({(int)httpResp.StatusCode}).");

            ResearchAiResponse? parsed = null;
            try { parsed = JsonSerializer.Deserialize<ResearchAiResponse>(body, Json); }
            catch { /* fall through to tolerant parsing */ }

            // If the body wasn't our envelope, try to recover structured models
            // directly from whatever JSON the backend returned.
            if (parsed is null || (parsed.Recommendations is null && parsed.ProposalDraft is null && string.IsNullOrWhiteSpace(parsed.ErrorMessage)))
            {
                var recovered = new ResearchAiResponse { Success = true, RawText = body };
                if (task == ResearchAiTaskType.Recommendations && _parser.TryParseRecommendations(body, out var r))
                    recovered.Recommendations = r;
                if (task == ResearchAiTaskType.ProposalDraft && _parser.TryParseProposal(body, out var d))
                    recovered.ProposalDraft = d;
                parsed = recovered;
            }

            if (!parsed.Success && !string.IsNullOrWhiteSpace(parsed.ErrorMessage))
                throw new ResearchAiException(parsed.ErrorMessage);

            return parsed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ResearchAiException("The Research AI service timed out. Please try again.");
        }
        catch (HttpRequestException)
        {
            throw new ResearchAiException("Could not reach the Research AI service. Check the endpoint in Settings and your connection.");
        }
    }
}

// ---------------------------------------------------------------------------
// Development-only mock. Enabled behind a settings toggle so the UI and flow
// can be tried before a real backend exists. It builds a basic scaffold ONLY
// from the project's own fields — it never invents data, results, p-values,
// or references, and it is clearly labelled as a development draft.
// ---------------------------------------------------------------------------
public sealed class MockResearchAiService : IResearchAiService
{
    public bool IsConfigured => true;

    public async Task<ResearchRecommendations> GenerateRecommendationsAsync(ResearchProject project, CancellationToken cancellationToken)
    {
        await Task.Delay(700, cancellationToken);   // let the loading state show
        return BuildRecommendations(project);
    }

    public async Task<ResearchProposalDraft> GenerateProposalDraftAsync(ResearchProject project, ResearchRecommendations? recommendations, CancellationToken cancellationToken)
    {
        await Task.Delay(700, cancellationToken);
        return BuildProposal(project, recommendations);
    }

    private static string Or(string value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static ResearchRecommendations BuildRecommendations(ResearchProject project)
    {
        string population = Or(project.Population, "the target population");
        string setting = Or(project.Setting, "the study setting");
        string specialty = Or(project.Specialty, "the relevant clinical field");
        string design = SuggestDesign(project);

        var rec = new ResearchRecommendations
        {
            SourceMode = ResearchSourceMode.DevelopmentMock,
            RefinedResearchTitle = Or(project.Title, "Untitled research project"),
            RecommendedStudyDesign = design,
            ResearchQuestion =
                $"Among {population} in {setting}, what does this study aim to describe or compare?",
            PrimaryObjective = Or(project.Aim,
                $"To describe the primary outcome of interest among {population}."),
            SecondaryObjectives = new List<string>
            {
                "Describe participant characteristics relevant to the aim.",
                "Explore associations between key variables (to be confirmed with your supervisor).",
                "Identify limitations that may affect interpretation."
            },
            InclusionCriteria = new List<string>
            {
                $"Members of {population} meeting the study definition.",
                "Availability of the data required for the primary outcome."
            },
            ExclusionCriteria = new List<string>
            {
                "Incomplete records for the primary outcome.",
                "Cases outside the defined time period or setting."
            },
            DataCollectionSuggestions = new List<string>
            {
                "Define each variable and its source before collection begins.",
                "Pilot the data-collection sheet on a few records first.",
                "Keep an anonymised ID for each record; avoid storing direct identifiers."
            },
            BiasAndLimitations = new List<string>
            {
                "Selection bias if the sample is not representative of " + population + ".",
                "Information/measurement bias from inconsistent data recording.",
                "Confounding — list variables to adjust for with your supervisor."
            },
            EthicsNotes = new List<string>
            {
                "Confirm whether institutional ethics/IRB approval is required.",
                "Plan for informed consent or a documented waiver where applicable.",
                "Store data securely and keep it de-identified."
            },
            NextSteps = new List<string>
            {
                "Review and refine this draft with your supervisor.",
                "Confirm the study design and finalise the variable list.",
                "Prepare the data-extraction sheet in the next phase."
            }
        };

        rec.SuggestedVariables.Add(new ResearchVariableSuggestion
        {
            VariableName = "age", VariableLabel = "Age", VariableType = "Continuous (years)",
            Role = "Demographic", SuggestedCoding = "Whole years", Notes = "Common baseline characteristic."
        });
        rec.SuggestedVariables.Add(new ResearchVariableSuggestion
        {
            VariableName = "sex", VariableLabel = "Sex", VariableType = "Categorical",
            Role = "Demographic", SuggestedCoding = "0 = Male, 1 = Female", Notes = "Adjust categories to your protocol."
        });
        rec.SuggestedVariables.Add(new ResearchVariableSuggestion
        {
            VariableName = "primary_outcome", VariableLabel = "Primary outcome (define)", VariableType = "To be defined",
            Role = "Outcome", SuggestedCoding = "Define with your supervisor",
            Notes = "This must reflect your actual aim in " + specialty + "."
        });

        rec.SuggestedAnalyses.Add(new ResearchAnalysisSuggestion
        {
            AnalysisName = "Descriptive statistics", WhenToUse = "Always, to summarise the sample.",
            VariablesNeeded = "All baseline and outcome variables.",
            OutputExpected = "Means/SD, medians/IQR, counts and percentages.", Notes = "Run only on real collected data."
        });
        rec.SuggestedAnalyses.Add(new ResearchAnalysisSuggestion
        {
            AnalysisName = "Group comparison (e.g. chi-square / t-test)", WhenToUse = "When comparing an outcome across groups.",
            VariablesNeeded = "One grouping variable and one outcome variable.",
            OutputExpected = "Test statistic and p-value from real data.", Notes = "Confirm assumptions before choosing the exact test."
        });

        return rec;
    }

    private static ResearchProposalDraft BuildProposal(ResearchProject project, ResearchRecommendations? recommendations)
    {
        string population = Or(project.Population, "the target population");
        string setting = Or(project.Setting, "the study setting");
        string design = recommendations is not null && !string.IsNullOrWhiteSpace(recommendations.RecommendedStudyDesign)
            ? recommendations.RecommendedStudyDesign
            : SuggestDesign(project);
        string title = recommendations is not null && !string.IsNullOrWhiteSpace(recommendations.RefinedResearchTitle)
            ? recommendations.RefinedResearchTitle
            : Or(project.Title, "Untitled research project");

        string Lines(List<string>? items, string fallback)
            => items is { Count: > 0 } ? string.Join(Environment.NewLine, items.Select(x => "• " + x)) : fallback;

        return new ResearchProposalDraft
        {
            SourceMode = ResearchSourceMode.DevelopmentMock,
            IsTemplateGenerated = true,
            Title = title,
            Background =
                $"Provide the clinical/academic background for this study in {Or(project.Specialty, "your field")}. " +
                "Summarise what is already known and add verified references yourself.",
            Rationale =
                "Explain the gap this study addresses and why it matters for " + population + ".",
            Aim = Or(project.Aim, "State the single overall aim of the study."),
            Objectives = recommendations is not null
                ? Lines(recommendations.SecondaryObjectives.Prepend(recommendations.PrimaryObjective).Where(s => !string.IsNullOrWhiteSpace(s)).ToList(),
                    "List the primary and secondary objectives.")
                : "List the primary and secondary objectives.",
            Methods = $"Describe how the study will be conducted using a {design}.",
            StudyDesign = design,
            Setting = setting,
            Population = population,
            InclusionCriteria = recommendations is not null
                ? Lines(recommendations.InclusionCriteria, "Define who is eligible.")
                : "Define who is eligible.",
            ExclusionCriteria = recommendations is not null
                ? Lines(recommendations.ExclusionCriteria, "Define who is excluded and why.")
                : "Define who is excluded and why.",
            Variables = recommendations is { SuggestedVariables.Count: > 0 }
                ? string.Join(Environment.NewLine, recommendations.SuggestedVariables.Select(v => "• " + v.HeaderDisplay + " (" + Or(v.Role, "role") + ")"))
                : "List each variable, its type, and its role.",
            DataCollection = "Describe the data-collection tool and process. Pilot it before full collection.",
            StatisticalAnalysisPlan = "State the planned analyses. Do not report any results here — the study has not been run.",
            Ethics = "State ethics/IRB approval status, consent process, and data protection measures.",
            Timeline = "Outline the phases and approximate duration of each.",
            Limitations = "Note anticipated limitations (design, sampling, measurement)."
        };
    }

    private static string SuggestDesign(ResearchProject project)
    {
        string st = (project.StudyType ?? "").Trim().ToLowerInvariant();
        if (st.Contains("cohort")) return "Cohort study";
        if (st.Contains("case")) return "Case–control study";
        if (st.Contains("cross")) return "Cross-sectional study";
        if (st.Contains("trial") || st.Contains("rct")) return "Randomised controlled trial";
        if (st.Contains("review") || st.Contains("meta")) return "Systematic review";
        if (st.Contains("qualitative")) return "Qualitative study";
        return "Cross-sectional (descriptive) study — confirm with your supervisor";
    }
}

// ---------------------------------------------------------------------------
// Response parser. Reads whatever JSON the backend returns and maps it into the
// app models. Deliberately forgiving: it extracts the first balanced JSON
// object even if wrapped in fences or prose, and never throws on bad input.
// ---------------------------------------------------------------------------
public interface IResearchRecommendationParser
{
    bool TryParseRecommendations(string text, out ResearchRecommendations recommendations);
    bool TryParseProposal(string text, out ResearchProposalDraft proposal);
}

public sealed class ResearchRecommendationParser : IResearchRecommendationParser
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public bool TryParseRecommendations(string text, out ResearchRecommendations recommendations)
    {
        recommendations = new ResearchRecommendations { SourceMode = ResearchSourceMode.AiGenerated };

        if (!TryExtractJson(text, out string json)) return false;

        try
        {
            var dto = JsonSerializer.Deserialize<RecDto>(json, Opts);
            if (dto is null) return false;

            recommendations.RefinedResearchTitle = Clean(dto.RefinedTitle);
            recommendations.RecommendedStudyDesign = Clean(dto.StudyDesign);
            recommendations.ResearchQuestion = Clean(dto.ResearchQuestion);
            recommendations.PrimaryObjective = Clean(dto.PrimaryObjective);
            recommendations.SecondaryObjectives = CleanList(dto.SecondaryObjectives);
            recommendations.InclusionCriteria = CleanList(dto.InclusionCriteria);
            recommendations.ExclusionCriteria = CleanList(dto.ExclusionCriteria);
            recommendations.DataCollectionSuggestions = CleanList(dto.DataCollection);
            recommendations.BiasAndLimitations = CleanList(dto.BiasAndLimitations);
            recommendations.EthicsNotes = CleanList(dto.EthicsNotes);
            recommendations.NextSteps = CleanList(dto.NextSteps);

            if (dto.Variables is not null)
                foreach (var v in dto.Variables)
                {
                    if (v is null) continue;
                    recommendations.SuggestedVariables.Add(new ResearchVariableSuggestion
                    {
                        VariableName = Clean(v.Name),
                        VariableLabel = Clean(v.Label),
                        VariableType = Clean(v.Type),
                        Role = Clean(v.Role),
                        SuggestedCoding = Clean(v.Coding),
                        Notes = Clean(v.Notes)
                    });
                }

            if (dto.Analyses is not null)
                foreach (var a in dto.Analyses)
                {
                    if (a is null) continue;
                    recommendations.SuggestedAnalyses.Add(new ResearchAnalysisSuggestion
                    {
                        AnalysisName = Clean(a.Name),
                        WhenToUse = Clean(a.WhenToUse),
                        VariablesNeeded = Clean(a.VariablesNeeded),
                        OutputExpected = Clean(a.OutputExpected),
                        Notes = Clean(a.Notes)
                    });
                }

            recommendations.RawAiText = text.Trim();
            return recommendations.HasStructuredContent;
        }
        catch
        {
            return false;
        }
    }

    public bool TryParseProposal(string text, out ResearchProposalDraft proposal)
    {
        proposal = new ResearchProposalDraft
        {
            SourceMode = ResearchSourceMode.AiGenerated,
            IsTemplateGenerated = false
        };

        if (!TryExtractJson(text, out string json)) return false;

        try
        {
            var dto = JsonSerializer.Deserialize<ProposalDto>(json, Opts);
            if (dto is null) return false;

            proposal.Title = Clean(dto.Title);
            proposal.Background = Clean(dto.Background);
            proposal.Rationale = Clean(dto.Rationale);
            proposal.Aim = Clean(dto.Aim);
            proposal.Objectives = Clean(dto.Objectives);
            proposal.Methods = Clean(dto.Methods);
            proposal.StudyDesign = Clean(dto.StudyDesign);
            proposal.Setting = Clean(dto.Setting);
            proposal.Population = Clean(dto.Population);
            proposal.InclusionCriteria = Clean(dto.InclusionCriteria);
            proposal.ExclusionCriteria = Clean(dto.ExclusionCriteria);
            proposal.Variables = Clean(dto.Variables);
            proposal.DataCollection = Clean(dto.DataCollection);
            proposal.StatisticalAnalysisPlan = Clean(dto.StatisticalAnalysisPlan);
            proposal.Ethics = Clean(dto.Ethics);
            proposal.Timeline = Clean(dto.Timeline);
            proposal.Limitations = Clean(dto.Limitations);

            return !string.IsNullOrWhiteSpace(proposal.Title)
                || !string.IsNullOrWhiteSpace(proposal.Background)
                || !string.IsNullOrWhiteSpace(proposal.Aim)
                || !string.IsNullOrWhiteSpace(proposal.Methods)
                || !string.IsNullOrWhiteSpace(proposal.Objectives)
                || !string.IsNullOrWhiteSpace(proposal.StudyDesign);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractJson(string text, out string json)
    {
        json = "";
        if (string.IsNullOrWhiteSpace(text)) return false;

        int start = text.IndexOf('{');
        if (start < 0) return false;

        int depth = 0;
        bool inString = false, escape = false;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) { json = text.Substring(start, i - start + 1); return true; }
            }
        }
        return false;
    }

    private static string Clean(string? s) => (s ?? "").Trim();

    private static List<string> CleanList(List<string>? items)
    {
        var list = new List<string>();
        if (items is null) return list;
        foreach (var item in items)
        {
            string t = (item ?? "").Trim();
            if (t.Length > 0) list.Add(t);
        }
        return list;
    }

    private sealed class RecDto
    {
        [JsonPropertyName("refinedTitle")] public string? RefinedTitle { get; set; }
        [JsonPropertyName("studyDesign")] public string? StudyDesign { get; set; }
        [JsonPropertyName("researchQuestion")] public string? ResearchQuestion { get; set; }
        [JsonPropertyName("primaryObjective")] public string? PrimaryObjective { get; set; }
        [JsonPropertyName("secondaryObjectives")] public List<string>? SecondaryObjectives { get; set; }
        [JsonPropertyName("variables")] public List<VarDto?>? Variables { get; set; }
        [JsonPropertyName("analyses")] public List<AnaDto?>? Analyses { get; set; }
        [JsonPropertyName("inclusionCriteria")] public List<string>? InclusionCriteria { get; set; }
        [JsonPropertyName("exclusionCriteria")] public List<string>? ExclusionCriteria { get; set; }
        [JsonPropertyName("dataCollection")] public List<string>? DataCollection { get; set; }
        [JsonPropertyName("biasAndLimitations")] public List<string>? BiasAndLimitations { get; set; }
        [JsonPropertyName("ethicsNotes")] public List<string>? EthicsNotes { get; set; }
        [JsonPropertyName("nextSteps")] public List<string>? NextSteps { get; set; }
    }

    private sealed class VarDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("role")] public string? Role { get; set; }
        [JsonPropertyName("coding")] public string? Coding { get; set; }
        [JsonPropertyName("notes")] public string? Notes { get; set; }
    }

    private sealed class AnaDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("whenToUse")] public string? WhenToUse { get; set; }
        [JsonPropertyName("variablesNeeded")] public string? VariablesNeeded { get; set; }
        [JsonPropertyName("outputExpected")] public string? OutputExpected { get; set; }
        [JsonPropertyName("notes")] public string? Notes { get; set; }
    }

    private sealed class ProposalDto
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("background")] public string? Background { get; set; }
        [JsonPropertyName("rationale")] public string? Rationale { get; set; }
        [JsonPropertyName("aim")] public string? Aim { get; set; }
        [JsonPropertyName("objectives")] public string? Objectives { get; set; }
        [JsonPropertyName("methods")] public string? Methods { get; set; }
        [JsonPropertyName("studyDesign")] public string? StudyDesign { get; set; }
        [JsonPropertyName("setting")] public string? Setting { get; set; }
        [JsonPropertyName("population")] public string? Population { get; set; }
        [JsonPropertyName("inclusionCriteria")] public string? InclusionCriteria { get; set; }
        [JsonPropertyName("exclusionCriteria")] public string? ExclusionCriteria { get; set; }
        [JsonPropertyName("variables")] public string? Variables { get; set; }
        [JsonPropertyName("dataCollection")] public string? DataCollection { get; set; }
        [JsonPropertyName("statisticalAnalysisPlan")] public string? StatisticalAnalysisPlan { get; set; }
        [JsonPropertyName("ethics")] public string? Ethics { get; set; }
        [JsonPropertyName("timeline")] public string? Timeline { get; set; }
        [JsonPropertyName("limitations")] public string? Limitations { get; set; }
    }
}
