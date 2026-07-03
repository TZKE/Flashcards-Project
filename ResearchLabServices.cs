using System.Net.Http;
using System.Net.Http.Headers;
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

// Persisted to research_ai_config.json (separate from the flashcard AI
// settings, which are never touched by the Research Lab).
public sealed class ResearchAiOptions
{
    public string EndpointBaseUrl { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 180;
    public bool UseDevelopmentMock { get; set; }

    // DEVELOPMENT-ONLY switch. When enabled, the in-app Research AI workflow is
    // routed to a direct chat-completions provider (see
    // DevelopmentZaiResearchAiService) using the API key the user already saved
    // for flashcard generation. This is for local testing before the backend
    // proxy exists; production must use EndpointBaseUrl. The provider name is
    // never shown in the UI — the corresponding checkbox is provider-neutral.
    public bool UseDevelopmentZaiProvider { get; set; }

    [JsonIgnore]
    public bool IsConfigured =>
        UseDevelopmentMock || UseDevelopmentZaiProvider || !string.IsNullOrWhiteSpace(EndpointBaseUrl);

    public ResearchAiOptions Clone() => new()
    {
        EndpointBaseUrl = EndpointBaseUrl,
        TimeoutSeconds = TimeoutSeconds,
        UseDevelopmentMock = UseDevelopmentMock,
        UseDevelopmentZaiProvider = UseDevelopmentZaiProvider
    };
}

// Read-only credentials supplied by the host so the development provider can
// reuse the API key the user already configured for flashcards. This object is
// only ever read — the development service never writes it back, never logs it,
// and never surfaces the key in any message or in the UI. No key is stored in
// this file or hardcoded anywhere.
public sealed class ZaiDevCredentials
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.z.ai/api/paas/v4";
    public string Model { get; set; } = "GLM-4.7-FlashX";

    [JsonIgnore]
    public bool HasKey => !string.IsNullOrWhiteSpace(ApiKey);
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

// Facade that reads live options and dispatches to the backend HTTP client, the
// development mock, or the development provider. It never contains provider keys
// itself — the development provider receives credentials through a read-only
// callback supplied by the host.
public sealed class ResearchAiService : IResearchAiService
{
    private readonly Func<ResearchAiOptions> _options;
    private readonly MockResearchAiService _mock = new();
    private readonly ResearchAiHttpClient _http;
    private readonly DevelopmentZaiResearchAiService? _devProvider;

    public ResearchAiService(Func<ResearchAiOptions> optionsProvider, Func<ZaiDevCredentials>? devCredentials = null)
    {
        _options = optionsProvider;
        _http = new ResearchAiHttpClient(optionsProvider);
        if (devCredentials is not null)
            _devProvider = new DevelopmentZaiResearchAiService(optionsProvider, devCredentials);
    }

    public bool IsConfigured => _options().IsConfigured;

    // Chooses the active implementation for the current options. The development
    // provider takes precedence when explicitly enabled (real provider testing),
    // then the offline mock, then the production backend endpoint.
    private IResearchAiService Route(ResearchAiOptions o)
    {
        if (!o.IsConfigured) throw new ResearchAiNotConfiguredException();
        if (o.UseDevelopmentZaiProvider && _devProvider is not null) return _devProvider;
        if (o.UseDevelopmentMock) return _mock;
        return _http;
    }

    public Task<ResearchRecommendations> GenerateRecommendationsAsync(ResearchProject project, CancellationToken cancellationToken)
        => Route(_options()).GenerateRecommendationsAsync(project, cancellationToken);

    public Task<ResearchProposalDraft> GenerateProposalDraftAsync(ResearchProject project, ResearchRecommendations? recommendations, CancellationToken cancellationToken)
        => Route(_options()).GenerateProposalDraftAsync(project, recommendations, cancellationToken);
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
// DEVELOPMENT-ONLY provider.
//
// Connects the in-app Research AI workflow directly to a chat-completions
// provider so the flow can be tested locally before the backend proxy exists.
// It is NOT the production path: in production the app must call the backend
// (ResearchAiHttpClient) so provider secrets never live in the desktop client.
//
// SECURITY / SAFETY:
//   * The API key is READ (never written) from credentials the host supplies,
//     reusing the key the user already saved for flashcard generation. No key
//     is hardcoded here, and the key is never printed, logged, or included in
//     any exception message or UI text.
//   * The provider name is never surfaced to the user; the Research Lab UI and
//     every message here stay provider-neutral ("Research AI").
//   * The prompts require strict JSON and forbid fabricating data, p-values,
//     results, or references.
//   * All failure modes (missing key, invalid key, timeout, network error,
//     rate limit, non-JSON, empty response) are mapped to friendly, key-free
//     ResearchAiException messages — the service never crashes the app.
// ---------------------------------------------------------------------------
public sealed class DevelopmentZaiResearchAiService : IResearchAiService
{
    // Disable the HttpClient-level timeout (its default is 100s, which was
    // cutting off the ~90-100s structured-JSON generations). The real per-request
    // timeout is enforced by the linked CancellationTokenSource below, mirroring
    // the generous allowance the working flashcard request uses.
    private static readonly HttpClient Http = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    private readonly Func<ResearchAiOptions> _options;
    private readonly Func<ZaiDevCredentials> _credentials;
    private readonly ResearchRecommendationParser _parser = new();

    public DevelopmentZaiResearchAiService(Func<ResearchAiOptions> options, Func<ZaiDevCredentials> credentials)
    {
        _options = options;
        _credentials = credentials;
    }

    public bool IsConfigured => _credentials().HasKey;

    public async Task<ResearchRecommendations> GenerateRecommendationsAsync(ResearchProject project, CancellationToken cancellationToken)
    {
        string text = await CompleteAsync(RecommendationsSystemPrompt, BuildRecommendationsUserPrompt(project), cancellationToken);

        if (!_parser.TryParseRecommendations(text, out var rec) || !rec.HasStructuredContent)
            throw new ResearchAiException("The Research AI response could not be read as recommendations.");

        rec.SourceMode = ResearchSourceMode.AiGenerated;
        return rec;
    }

    public async Task<ResearchProposalDraft> GenerateProposalDraftAsync(ResearchProject project, ResearchRecommendations? recommendations, CancellationToken cancellationToken)
    {
        string text = await CompleteAsync(ProposalSystemPrompt, BuildProposalUserPrompt(project, recommendations), cancellationToken);

        if (!_parser.TryParseProposal(text, out var draft))
            throw new ResearchAiException("The Research AI response could not be read as a proposal draft.");

        draft.SourceMode = ResearchSourceMode.AiGenerated;
        draft.IsTemplateGenerated = false;
        return draft;
    }

    private async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        var creds = _credentials();
        if (!creds.HasKey)
            throw new ResearchAiException("No AI provider key is available. Add your API key in AI Settings, then try again.");

        var opts = _options();
        string baseUrl = string.IsNullOrWhiteSpace(creds.BaseUrl)
            ? "https://api.z.ai/api/paas/v4"
            : creds.BaseUrl.Trim().TrimEnd('/');
        string model = string.IsNullOrWhiteSpace(creds.Model) ? "GLM-4.7-FlashX" : creds.Model.Trim();

        var body = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            temperature = 0.2,
            max_tokens = 8000,   // headroom so the large research JSON is never truncated
            stream = false
        };

        // GLM takes ~90-100s to produce the full structured JSON, so never let a
        // small configured timeout cut it off. Floor at 180s (matching the proven
        // flashcard request's 3-minute allowance); users may raise it up to 300s.
        int timeoutSeconds = Math.Clamp(Math.Max(opts.TimeoutSeconds, 180), 30, 300);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
        // The key is attached only to the outgoing request header — never logged.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.ApiKey.Trim());
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        string json;
        try
        {
            response = await Http.SendAsync(request, cts.Token);
            json = await response.Content.ReadAsStringAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ResearchAiException("The Research AI request timed out. Please try again.");
        }
        catch (HttpRequestException)
        {
            throw new ResearchAiException("Could not reach the Research AI service. Check your connection and try again.");
        }

        if (!response.IsSuccessStatusCode)
        {
            // Map common statuses to friendly, key-free messages. The raw
            // provider body is intentionally NOT included (it can echo details).
            throw new ResearchAiException((int)response.StatusCode switch
            {
                401 or 403 => "The Research AI provider rejected the request. Check your API key in AI Settings.",
                429 => "The Research AI provider is busy (rate limited). Wait a moment and try again.",
                >= 500 => "The Research AI provider had a server error. Please try again shortly.",
                _ => "The Research AI provider returned an error. Please try again."
            });
        }

        string content = ExtractContent(json);
        if (string.IsNullOrWhiteSpace(content))
            throw new ResearchAiException("The Research AI service returned an empty response. Please try again.");

        return content;
    }

    // Reads choices[0].message.content from a standard chat-completions body.
    private static string ExtractContent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("message", out var message)
                    && message.TryGetProperty("content", out var contentEl)
                    && contentEl.ValueKind == JsonValueKind.String)
                {
                    return contentEl.GetString()?.Trim() ?? "";
                }
            }
        }
        catch
        {
            // Non-JSON / unexpected envelope — nothing usable.
        }
        return "";
    }

    // ---- Prompts: strict JSON only, no fabricated data/results/references ----

    private const string SafetyRules =
        "Safety rules you must follow: do NOT fabricate data, results, numbers, " +
        "p-values, sample sizes, or statistics; do NOT invent references or " +
        "citations; do NOT report findings for a study that has not been run. " +
        "This is methodological guidance only. If required information is " +
        "missing, state what the student needs to provide in the relevant field " +
        "instead of guessing. Return ONLY one valid JSON object — no markdown, " +
        "no code fences, no commentary before or after the JSON.";

    private const string RecommendationsSystemPrompt =
        "You are a research methodology assistant for medical and university " +
        "students. " + SafetyRules + "\n\n" +
        "Use exactly this JSON shape (include every field; use empty strings or " +
        "empty arrays where a value genuinely does not apply):\n" +
        "{\n" +
        "  \"refinedTitle\": \"string\",\n" +
        "  \"studyDesign\": \"string\",\n" +
        "  \"researchQuestion\": \"string\",\n" +
        "  \"primaryObjective\": \"string\",\n" +
        "  \"secondaryObjectives\": [\"string\"],\n" +
        "  \"variables\": [{\"name\":\"string\",\"label\":\"string\",\"type\":\"string\",\"role\":\"string\",\"coding\":\"string\",\"notes\":\"string\"}],\n" +
        "  \"analyses\": [{\"name\":\"string\",\"whenToUse\":\"string\",\"variablesNeeded\":\"string\",\"outputExpected\":\"string\",\"notes\":\"string\"}],\n" +
        "  \"inclusionCriteria\": [\"string\"],\n" +
        "  \"exclusionCriteria\": [\"string\"],\n" +
        "  \"dataCollection\": [\"string\"],\n" +
        "  \"biasAndLimitations\": [\"string\"],\n" +
        "  \"ethicsNotes\": [\"string\"],\n" +
        "  \"nextSteps\": [\"string\"]\n" +
        "}\n\n" +
        "Return ONLY the JSON object above, filled in. No markdown, no code fences, " +
        "no explanation, and no text before or after the JSON.";

    private const string ProposalSystemPrompt =
        "You are drafting a research PROPOSAL (protocol) for a medical or " +
        "university student. The study has NOT been conducted yet. " + SafetyRules +
        " Additional rules for the proposal: do NOT invent references, results, " +
        "p-values, or data; do NOT write a Results or Discussion section and do " +
        "NOT include any findings; where a citation is needed, insert a bracketed " +
        "placeholder like [add reference] instead of a real or made-up citation. " +
        "The statisticalAnalysisPlan field must describe planned analyses only and " +
        "must not contain any results. The draft is a starting point that the " +
        "student and their supervisor must review and complete.\n\n" +
        "Use exactly this JSON shape (all values are strings; use \"\" where a " +
        "value genuinely does not apply):\n" +
        "{\n" +
        "  \"title\": \"string\",\n" +
        "  \"background\": \"string\",\n" +
        "  \"rationale\": \"string\",\n" +
        "  \"aim\": \"string\",\n" +
        "  \"objectives\": \"string\",\n" +
        "  \"methods\": \"string\",\n" +
        "  \"studyDesign\": \"string\",\n" +
        "  \"setting\": \"string\",\n" +
        "  \"population\": \"string\",\n" +
        "  \"inclusionCriteria\": \"string\",\n" +
        "  \"exclusionCriteria\": \"string\",\n" +
        "  \"variables\": \"string\",\n" +
        "  \"dataCollection\": \"string\",\n" +
        "  \"statisticalAnalysisPlan\": \"string\",\n" +
        "  \"ethics\": \"string\",\n" +
        "  \"timeline\": \"string\",\n" +
        "  \"limitations\": \"string\"\n" +
        "}\n\n" +
        "Return ONLY the JSON object above, filled in. No markdown, no code fences, " +
        "no explanation, and no text before or after the JSON.";

    private static string BuildRecommendationsUserPrompt(ResearchProject p)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Produce methodological recommendations for this student research project.");
        sb.AppendLine("Base everything only on the details below; where a detail is missing, say what is needed rather than inventing it.");
        sb.AppendLine();
        AppendProjectDetails(sb, p);
        return sb.ToString();
    }

    private static string BuildProposalUserPrompt(ResearchProject p, ResearchRecommendations? rec)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Draft a research proposal (protocol) for this student research project.");
        sb.AppendLine("Base everything only on the details below; where a detail is missing, say what is needed rather than inventing it.");
        sb.AppendLine();
        AppendProjectDetails(sb, p);

        if (rec is not null && rec.HasStructuredContent)
        {
            sb.AppendLine();
            sb.AppendLine("ACCEPTED RESEARCH PLAN — the student has reviewed and accepted the following. "
                + "Build the proposal directly from it and stay fully consistent with every item:");
            if (!string.IsNullOrWhiteSpace(rec.RefinedResearchTitle)) sb.AppendLine("- Refined title: " + rec.RefinedResearchTitle);
            if (!string.IsNullOrWhiteSpace(rec.ResearchQuestion)) sb.AppendLine("- Research question: " + rec.ResearchQuestion);
            if (!string.IsNullOrWhiteSpace(rec.RecommendedStudyDesign)) sb.AppendLine("- Study design: " + rec.RecommendedStudyDesign);
            if (!string.IsNullOrWhiteSpace(rec.PrimaryObjective)) sb.AppendLine("- Primary objective: " + rec.PrimaryObjective);
            if (rec.SecondaryObjectives.Count > 0) sb.AppendLine("- Secondary objectives: " + string.Join("; ", rec.SecondaryObjectives));

            if (rec.SuggestedVariables.Count > 0)
            {
                sb.AppendLine("- Variables:");
                foreach (var v in rec.SuggestedVariables)
                {
                    var meta = new List<string>();
                    if (!string.IsNullOrWhiteSpace(v.VariableType)) meta.Add(v.VariableType.Trim());
                    if (!string.IsNullOrWhiteSpace(v.Role)) meta.Add(v.Role.Trim());
                    if (!string.IsNullOrWhiteSpace(v.SuggestedCoding)) meta.Add("coded as " + v.SuggestedCoding.Trim());
                    sb.AppendLine("    • " + v.HeaderDisplay + (meta.Count > 0 ? " (" + string.Join(", ", meta) + ")" : ""));
                }
            }

            if (rec.SuggestedAnalyses.Count > 0)
            {
                sb.AppendLine("- Suggested analyses:");
                foreach (var a in rec.SuggestedAnalyses)
                {
                    string when = string.IsNullOrWhiteSpace(a.WhenToUse) ? "" : " — " + a.WhenToUse.Trim();
                    sb.AppendLine("    • " + a.HeaderDisplay + when);
                }
            }

            AppendPlanList(sb, "Inclusion criteria", rec.InclusionCriteria);
            AppendPlanList(sb, "Exclusion criteria", rec.ExclusionCriteria);
            AppendPlanList(sb, "Data collection suggestions", rec.DataCollectionSuggestions);
            AppendPlanList(sb, "Ethics notes", rec.EthicsNotes);
            AppendPlanList(sb, "Bias and limitations", rec.BiasAndLimitations);
            AppendPlanList(sb, "Next steps", rec.NextSteps);
        }

        return sb.ToString();
    }

    private static void AppendPlanList(StringBuilder sb, string label, List<string> items)
    {
        if (items is null || items.Count == 0) return;
        sb.AppendLine("- " + label + ":");
        foreach (var item in items)
            if (!string.IsNullOrWhiteSpace(item))
                sb.AppendLine("    • " + item.Trim());
    }

    private static void AppendProjectDetails(StringBuilder sb, ResearchProject p)
    {
        string V(string s) => string.IsNullOrWhiteSpace(s) ? "(not provided)" : s.Trim();
        sb.AppendLine("Project details:");
        sb.AppendLine("- Title: " + V(p.Title));
        sb.AppendLine("- Specialty / field: " + V(p.Specialty));
        sb.AppendLine("- Study type (student's idea): " + V(p.StudyType));
        sb.AppendLine("- Aim: " + V(p.Aim));
        sb.AppendLine("- Population: " + V(p.Population));
        sb.AppendLine("- Setting: " + V(p.Setting));
        sb.AppendLine("- Time period: " + V(p.TimePeriod));
        sb.AppendLine("- Available data type: " + V(p.AvailableDataType));
        sb.AppendLine("- Desired outputs: " + (p.DesiredOutputs.Count > 0 ? string.Join(", ", p.DesiredOutputs) : "(not provided)"));
        sb.AppendLine("- Notes: " + V(p.Notes));
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

        // The model may wrap the JSON in prose or code fences, or emit more than
        // one object. Try every balanced object (largest first) and keep the first
        // that yields real structured content.
        foreach (string json in ExtractJsonCandidates(text))
        {
            try
            {
                var dto = JsonSerializer.Deserialize<RecDto>(json, Opts);
                if (dto is null) continue;

                var r = new ResearchRecommendations
                {
                    SourceMode = ResearchSourceMode.AiGenerated,
                    RefinedResearchTitle = Clean(dto.RefinedTitle),
                    RecommendedStudyDesign = Clean(dto.StudyDesign),
                    ResearchQuestion = Clean(dto.ResearchQuestion),
                    PrimaryObjective = Clean(dto.PrimaryObjective),
                    SecondaryObjectives = CleanList(dto.SecondaryObjectives),
                    InclusionCriteria = CleanList(dto.InclusionCriteria),
                    ExclusionCriteria = CleanList(dto.ExclusionCriteria),
                    DataCollectionSuggestions = CleanList(dto.DataCollection),
                    BiasAndLimitations = CleanList(dto.BiasAndLimitations),
                    EthicsNotes = CleanList(dto.EthicsNotes),
                    NextSteps = CleanList(dto.NextSteps),
                    RawAiText = text.Trim()
                };

                if (dto.Variables is not null)
                    foreach (var v in dto.Variables)
                    {
                        if (v is null) continue;
                        r.SuggestedVariables.Add(new ResearchVariableSuggestion
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
                        r.SuggestedAnalyses.Add(new ResearchAnalysisSuggestion
                        {
                            AnalysisName = Clean(a.Name),
                            WhenToUse = Clean(a.WhenToUse),
                            VariablesNeeded = Clean(a.VariablesNeeded),
                            OutputExpected = Clean(a.OutputExpected),
                            Notes = Clean(a.Notes)
                        });
                    }

                if (r.HasStructuredContent)
                {
                    recommendations = r;
                    return true;
                }
            }
            catch
            {
                // Not this candidate — try the next one.
            }
        }

        return false;
    }

    public bool TryParseProposal(string text, out ResearchProposalDraft proposal)
    {
        proposal = new ResearchProposalDraft
        {
            SourceMode = ResearchSourceMode.AiGenerated,
            IsTemplateGenerated = false
        };

        foreach (string json in ExtractJsonCandidates(text))
        {
            try
            {
                var dto = JsonSerializer.Deserialize<ProposalDto>(json, Opts);
                if (dto is null) continue;

                var d = new ResearchProposalDraft
                {
                    SourceMode = ResearchSourceMode.AiGenerated,
                    IsTemplateGenerated = false,
                    Title = Clean(dto.Title),
                    Background = Clean(dto.Background),
                    Rationale = Clean(dto.Rationale),
                    Aim = Clean(dto.Aim),
                    Objectives = Clean(dto.Objectives),
                    Methods = Clean(dto.Methods),
                    StudyDesign = Clean(dto.StudyDesign),
                    Setting = Clean(dto.Setting),
                    Population = Clean(dto.Population),
                    InclusionCriteria = Clean(dto.InclusionCriteria),
                    ExclusionCriteria = Clean(dto.ExclusionCriteria),
                    Variables = Clean(dto.Variables),
                    DataCollection = Clean(dto.DataCollection),
                    StatisticalAnalysisPlan = Clean(dto.StatisticalAnalysisPlan),
                    Ethics = Clean(dto.Ethics),
                    Timeline = Clean(dto.Timeline),
                    Limitations = Clean(dto.Limitations)
                };

                bool hasContent = !string.IsNullOrWhiteSpace(d.Title)
                    || !string.IsNullOrWhiteSpace(d.Background)
                    || !string.IsNullOrWhiteSpace(d.Aim)
                    || !string.IsNullOrWhiteSpace(d.Methods)
                    || !string.IsNullOrWhiteSpace(d.Objectives)
                    || !string.IsNullOrWhiteSpace(d.StudyDesign);

                if (hasContent)
                {
                    proposal = d;
                    return true;
                }
            }
            catch
            {
                // Not this candidate — try the next one.
            }
        }

        return false;
    }

    // Returns every balanced top-level JSON object found in the text, largest
    // first (the real payload is almost always the biggest object). Markdown code
    // fences are stripped first, so ```json ... ``` and prose-wrapped replies both
    // work. Never throws.
    private static List<string> ExtractJsonCandidates(string text)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return results;

        string cleaned = text.Replace("```json", " ", StringComparison.OrdinalIgnoreCase)
                             .Replace("```", " ");

        int i = 0;
        while (i < cleaned.Length)
        {
            if (cleaned[i] != '{') { i++; continue; }

            int depth = 0;
            bool inString = false, escape = false;
            int end = -1;
            for (int j = i; j < cleaned.Length; j++)
            {
                char c = cleaned[j];
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
                    if (depth == 0) { end = j; break; }
                }
            }

            if (end < 0) break;   // no complete object from here on
            results.Add(cleaned.Substring(i, end - i + 1));
            i = end + 1;          // continue scanning after this top-level object
        }

        results.Sort((a, b) => b.Length.CompareTo(a.Length));
        return results;
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
