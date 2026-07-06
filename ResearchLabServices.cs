using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace AIFlashcardMaker;

// ---------------------------------------------------------------------------
// Shared timeout policy for every Research AI call path (dev provider + the
// production backend HTTP client). Centralized so no single call site can
// silently use a lower timeout than the others.
//
// IMPORTANT: complex research tasks (large proposals, big extraction sheets)
// can legitimately take many minutes. 180 seconds is kept only as a UI
// "this is taking a while" notice threshold — it is NOT a cutoff. The actual
// hard ceiling is 45 minutes (2700s); the default per-request ceiling is 15
// minutes (900s), and the user can raise it (up to the 45-minute max) in
// Research AI Settings. A saved low value from an older build (e.g. 60/180s)
// never cuts off a legitimate long-running request — Resolve() always floors
// at DefaultLongRunningSeconds.
// ---------------------------------------------------------------------------
internal static class ResearchAiTimeouts
{
    // UI-only "still working" notice threshold — never used as a cancellation limit.
    public const int StillWorkingNoticeSeconds = 180;

    public const int DefaultLongRunningSeconds = 900;    // 15 minutes
    public const int MaxLongRunningSeconds = 2700;       // 45 minutes

    public static int Resolve(int configuredSeconds, int floorSeconds = DefaultLongRunningSeconds)
        => Math.Clamp(Math.Max(configuredSeconds, floorSeconds), 30, MaxLongRunningSeconds);
}

// ---------------------------------------------------------------------------
// Developer-safe diagnostics for Research AI calls. Never includes API keys,
// request/response bodies, or proposal/project text — only call shape (action,
// timeout, elapsed time, provider mode, HTTP status, empty/parse flags). Written
// to a local log file and kept in memory so the UI can offer "Open details" on
// a failure without ever showing secrets.
// ---------------------------------------------------------------------------
internal static class ResearchAiDiagnostics
{
    public static string? LastEntry { get; private set; }

    public static string Log(string action, string providerMode, int timeoutSeconds, double elapsedSeconds,
        int? statusCode = null, bool bodyEmpty = false, string? parseError = null, string? outcome = null,
        string? model = null, int? inputChars = null, int? maxTokens = null, string? category = null)
    {
        string entry =
            $"[{DateTime.Now:u}] action={action} provider={providerMode} " +
            $"model={(string.IsNullOrWhiteSpace(model) ? "n/a" : model)} " +
            $"inputChars={(inputChars?.ToString() ?? "n/a")} maxTokens={(maxTokens?.ToString() ?? "n/a")} " +
            $"timeout={timeoutSeconds}s elapsed={elapsedSeconds:F1}s status={(statusCode?.ToString() ?? "n/a")} " +
            $"category={(category ?? "n/a")} emptyBody={bodyEmpty} parseError={(parseError ?? "none")} outcome={(outcome ?? "n/a")}";

        LastEntry = entry;
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIFlashcardMaker");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "research_ai.log"), entry + Environment.NewLine);
        }
        catch { /* logging must never throw */ }
        return entry;
    }
}

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
    public int TimeoutSeconds { get; set; } = ResearchAiTimeouts.DefaultLongRunningSeconds;
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

// Result of the lightweight "Test Research AI" health check (Part G). Never
// consumes meaningful tokens/quota and never throws — Settings always gets a
// result to display.
public sealed class ResearchAiHealthResult
{
    public bool Success { get; set; }
    // "Connected" | "Timed out" | "Unauthorized" | "Empty response" |
    // "Parse error" | "Provider unavailable" | "Not configured"
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
    public double ElapsedSeconds { get; set; }
}

public sealed class ResearchAiException : Exception
{
    // Developer-safe diagnostic line (see ResearchAiDiagnostics) the UI can show
    // behind an "Open details" action. Never contains keys or request content.
    public string? Diagnostics { get; }

    // True when this failure is specifically "ran out of the allowed time"
    // (our own timeout, or a provider/gateway timeout). The UI uses this flag
    // instead of matching against message text, so wording can change freely
    // without breaking the Retry/Continue-manually decision logic.
    public bool IsTimeout { get; }

    // Machine-readable failure category so the UI and diagnostics never have to
    // guess from message text. One of: "timeout" | "provider_error" |
    // "overload" | "rate_limit" | "prompt_too_long" | "empty_response" |
    // "parse_failure" | "network" | "auth" | "unknown".
    public string Category { get; }

    public ResearchAiException(string message, string? diagnostics = null, bool isTimeout = false, string category = "unknown") : base(message)
    {
        Diagnostics = diagnostics;
        IsTimeout = isTimeout;
        Category = category;
    }
}

// ---- Service abstraction --------------------------------------------------

public interface IResearchAiService
{
    bool IsConfigured { get; }
    Task<ResearchRecommendations> GenerateRecommendationsAsync(ResearchProject project, CancellationToken cancellationToken);
    Task<ResearchProposalDraft> GenerateProposalDraftAsync(ResearchProject project, ResearchRecommendations? recommendations, CancellationToken cancellationToken);

    // Phase 2F — Import Existing Proposal. Reads structured fields out of an
    // existing proposal the student supplied. Strictly extraction-only: the
    // implementation must never fabricate content that is not in the text.
    Task<ProposalExtractionResult> ExtractProposalAsync(string proposalText, ResearchProject existingProject, CancellationToken cancellationToken);

    // Phase 3 — Data Extraction Sheet. All three suggest a variable sheet / data
    // dictionary only. They must NEVER compute statistics, invent data, invent
    // results/p-values, or fabricate a sample size.
    //   * GenerateExtractionSheetAsync — build a sheet from the project, its
    //     imported proposal, accepted plan, CSV sample summary, and existing rows.
    //   * ExtractVariablesFromQuestionsAsync — turn questionnaire / Google Form
    //     questions into variable suggestions.
    //   * SuggestExtractionFixesAsync — propose corrections given a validation
    //     report and the current sheet.
    Task<ExtractionSheetResult> GenerateExtractionSheetAsync(ResearchProject project, string extraQuestionsContext, CancellationToken cancellationToken);
    Task<ExtractionSheetResult> ExtractVariablesFromQuestionsAsync(ResearchProject project, string questionsText, CancellationToken cancellationToken);
    Task<ExtractionSheetResult> SuggestExtractionFixesAsync(ResearchProject project, ExtractionValidationReport report, CancellationToken cancellationToken);

    // Phase 3 final stabilization — structured conflict fixes: ONE proposal per
    // active conflict (never a rewritten sheet), reviewed by the student before
    // anything is applied. Receives only compact conflict/sheet/header metadata.
    Task<ConflictFixResult> SuggestConflictFixesAsync(ResearchProject project, List<ConflictFixInput> conflicts, CancellationToken cancellationToken);

    // Lightweight connectivity check for Settings ("Test Research AI"). Minimal
    // tokens/payload; never throws.
    Task<ResearchAiHealthResult> TestConnectionAsync(CancellationToken cancellationToken);
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

    public Task<ProposalExtractionResult> ExtractProposalAsync(string proposalText, ResearchProject existingProject, CancellationToken cancellationToken)
        => Route(_options()).ExtractProposalAsync(proposalText, existingProject, cancellationToken);

    public Task<ExtractionSheetResult> GenerateExtractionSheetAsync(ResearchProject project, string extraQuestionsContext, CancellationToken cancellationToken)
        => Route(_options()).GenerateExtractionSheetAsync(project, extraQuestionsContext, cancellationToken);

    public Task<ExtractionSheetResult> ExtractVariablesFromQuestionsAsync(ResearchProject project, string questionsText, CancellationToken cancellationToken)
        => Route(_options()).ExtractVariablesFromQuestionsAsync(project, questionsText, cancellationToken);

    public Task<ExtractionSheetResult> SuggestExtractionFixesAsync(ResearchProject project, ExtractionValidationReport report, CancellationToken cancellationToken)
        => Route(_options()).SuggestExtractionFixesAsync(project, report, cancellationToken);

    public Task<ConflictFixResult> SuggestConflictFixesAsync(ResearchProject project, List<ConflictFixInput> conflicts, CancellationToken cancellationToken)
        => Route(_options()).SuggestConflictFixesAsync(project, conflicts, cancellationToken);

    // Health check tolerates "not configured" gracefully instead of throwing,
    // since Settings should always get a status back.
    public async Task<ResearchAiHealthResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var o = _options();
        if (!o.IsConfigured)
            return new ResearchAiHealthResult { Status = "Not configured", Message = "Add a backend endpoint, or enable the development mock/provider, then try again." };
        try { return await Route(o).TestConnectionAsync(cancellationToken); }
        catch (Exception ex) { return new ResearchAiHealthResult { Status = "Provider unavailable", Message = ex.Message }; }
    }
}

// ---- Backend/proxy HTTP client --------------------------------------------
// Calls a configurable endpoint. Assumes future routes:
//   POST {base}/api/research-ai/recommendations
//   POST {base}/api/research-ai/proposal
// No API keys are sent from the app; the backend owns provider secrets.
public sealed class ResearchAiHttpClient : IResearchAiService
{
    // Disable the HttpClient-level timeout (its default is 100s). The real
    // per-request timeout is enforced by the linked CancellationTokenSource
    // below via ResearchAiTimeouts, mirroring the development provider.
    private static readonly HttpClient Http = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
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

    public async Task<ProposalExtractionResult> ExtractProposalAsync(string proposalText, ResearchProject existingProject, CancellationToken cancellationToken)
    {
        var o = _options();
        if (string.IsNullOrWhiteSpace(o.EndpointBaseUrl))
            throw new ResearchAiNotConfiguredException();

        string url = o.EndpointBaseUrl.TrimEnd('/') + "/api/research-ai/extract-proposal";

        var request = new
        {
            projectId = existingProject.Id,
            taskType = "extract-proposal",
            project = ResearchAiProjectPayload.From(existingProject),
            proposalText,
            desiredOutputFormat = "json"
        };

        int timeoutSeconds = ResearchAiTimeouts.Resolve(o.TimeoutSeconds);
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(request, Json), Encoding.UTF8, "application/json");
            using var httpResp = await Http.PostAsync(url, content, cts.Token);
            string body = await httpResp.Content.ReadAsStringAsync(cts.Token);
            sw.Stop();

            if (!httpResp.IsSuccessStatusCode)
            {
                string diag = ResearchAiDiagnostics.Log("ExtractProposal", "Http", timeoutSeconds, sw.Elapsed.TotalSeconds, (int)httpResp.StatusCode, string.IsNullOrEmpty(body));
                throw new ResearchAiException((int)httpResp.StatusCode switch
                {
                    504 or 408 => "The provider stopped the request before completion. Your content was kept. Try again with the compact input or split the task.",
                    500 => $"Research AI provider returned an internal error after {sw.Elapsed.TotalSeconds:F0} seconds. Your content was kept. You can retry with compact input or continue manually.",
                    502 or 503 => "The Research AI provider is overloaded or temporarily unavailable. Your content was kept. Try again in a moment.",
                    _ => $"The Research AI service returned an error ({(int)httpResp.StatusCode})."
                }, diag,
                isTimeout: (int)httpResp.StatusCode is 504 or 408,
                category: (int)httpResp.StatusCode switch
                {
                    504 or 408 => "timeout",
                    502 or 503 => "overload",
                    429 => "rate_limit",
                    401 or 403 => "auth",
                    >= 500 => "provider_error",
                    _ => "provider_error"
                });
            }

            if (!_parser.TryParseExtraction(body, out var extraction) || !extraction.HasAnyContent)
            {
                string diag = ResearchAiDiagnostics.Log("ExtractProposal", "Http", timeoutSeconds, sw.Elapsed.TotalSeconds, (int)httpResp.StatusCode, string.IsNullOrEmpty(body), "could not parse extraction JSON");
                throw new ResearchAiException("The Research AI response could not be read as extracted proposal details.", diag);
            }

            ResearchAiDiagnostics.Log("ExtractProposal", "Http", timeoutSeconds, sw.Elapsed.TotalSeconds, (int)httpResp.StatusCode, outcome: "success");
            extraction.SourceMode = ResearchSourceMode.AiGenerated;
            return extraction;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            string diag = ResearchAiDiagnostics.Log("ExtractProposal", "Http", timeoutSeconds, sw.Elapsed.TotalSeconds, outcome: "timeout");
            throw new ResearchAiException("Research AI did not finish within the allowed time. Your content was kept. You can retry or continue manually.", diag, isTimeout: true);
        }
        catch (HttpRequestException)
        {
            sw.Stop();
            string diag = ResearchAiDiagnostics.Log("ExtractProposal", "Http", timeoutSeconds, sw.Elapsed.TotalSeconds, outcome: "network error");
            throw new ResearchAiException("Could not reach the Research AI service. Check the endpoint in Settings and your connection.", diag);
        }
    }

    // Phase 3 — Data Extraction Sheet endpoints. These follow the same
    // extraction pattern (POST a safe JSON payload, parse a strict-JSON reply).
    // The full CSV is never sent — only the privacy-safe CsvSampleSummary.
    public Task<ExtractionSheetResult> GenerateExtractionSheetAsync(ResearchProject project, string extraQuestionsContext, CancellationToken cancellationToken)
        => PostExtractionSheetAsync("extraction-sheet", project, new { extraQuestionsContext }, cancellationToken);

    public Task<ExtractionSheetResult> ExtractVariablesFromQuestionsAsync(ResearchProject project, string questionsText, CancellationToken cancellationToken)
        => PostExtractionSheetAsync("extraction-questions", project, new { questionsText }, cancellationToken);

    public Task<ExtractionSheetResult> SuggestExtractionFixesAsync(ResearchProject project, ExtractionValidationReport report, CancellationToken cancellationToken)
        => PostExtractionSheetAsync("extraction-fixes", project, new { report }, cancellationToken);

    // Structured per-conflict fixes. COMPACT payload only: the active conflicts,
    // the sheet variables (name/label/type/role/coding/aliases), CSV column
    // HEADERS, and short project context — never raw CSV rows or proposal text.
    public async Task<ConflictFixResult> SuggestConflictFixesAsync(ResearchProject project, List<ConflictFixInput> conflicts, CancellationToken cancellationToken)
    {
        var o = _options();
        if (string.IsNullOrWhiteSpace(o.EndpointBaseUrl))
            throw new ResearchAiNotConfiguredException();

        string url = o.EndpointBaseUrl.TrimEnd('/') + "/api/research-ai/conflict-fixes";
        var request = new
        {
            projectId = project.Id,
            taskType = "conflict-fixes",
            title = project.Title,
            specialty = project.Specialty,
            studyType = project.StudyType,
            variables = (project.Variables ?? new List<ResearchVariable>()).Select(v => new
            {
                v.VariableName, v.QuestionLabel, type = v.VariableType, role = v.Role, v.Coding, aliases = v.SourceColumnAliases
            }),
            csvHeaders = project.CsvSampleSummary?.Columns.Select(c => c.Name) ?? Enumerable.Empty<string>(),
            conflicts,
            desiredOutputFormat = "json"
        };

        int timeoutSeconds = ResearchAiTimeouts.Resolve(o.TimeoutSeconds);
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(request, Json), Encoding.UTF8, "application/json");
            using var httpResp = await Http.PostAsync(url, content, cts.Token);
            string body = await httpResp.Content.ReadAsStringAsync(cts.Token);
            sw.Stop();

            if (!httpResp.IsSuccessStatusCode)
            {
                string diag = ResearchAiDiagnostics.Log("ConflictFixes", "Http", timeoutSeconds, sw.Elapsed.TotalSeconds, (int)httpResp.StatusCode, string.IsNullOrEmpty(body));
                throw new ResearchAiException((int)httpResp.StatusCode switch
                {
                    504 or 408 => "The provider stopped the request before completion. Your content was kept. Try again with the compact input or split the task.",
                    500 => $"Research AI provider returned an internal error after {sw.Elapsed.TotalSeconds:F0} seconds. Your content was kept. You can retry with compact input or continue manually.",
                    502 or 503 => "The Research AI provider is overloaded or temporarily unavailable. Your content was kept. Try again in a moment.",
                    _ => $"The Research AI service returned an error ({(int)httpResp.StatusCode})."
                }, diag,
                isTimeout: (int)httpResp.StatusCode is 504 or 408,
                category: (int)httpResp.StatusCode switch
                {
                    504 or 408 => "timeout",
                    502 or 503 => "overload",
                    429 => "rate_limit",
                    401 or 403 => "auth",
                    >= 500 => "provider_error",
                    _ => "provider_error"
                });
            }

            if (!_parser.TryParseConflictFixes(body, out var result) || result.Fixes.Count == 0)
            {
                string diag = ResearchAiDiagnostics.Log("ConflictFixes", "Http", timeoutSeconds, sw.Elapsed.TotalSeconds, (int)httpResp.StatusCode, string.IsNullOrEmpty(body), "could not parse conflict fixes JSON", category: "parse_failure");
                throw new ResearchAiException("The Research AI response could not be read as conflict fixes. Please try again.", diag, category: "parse_failure");
            }

            ResearchAiDiagnostics.Log("ConflictFixes", "Http", timeoutSeconds, sw.Elapsed.TotalSeconds, (int)httpResp.StatusCode, outcome: "success");
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            string diag = ResearchAiDiagnostics.Log("ConflictFixes", "Http", timeoutSeconds, sw.Elapsed.TotalSeconds, outcome: "timeout", category: "timeout");
            throw new ResearchAiException("Research AI did not finish within the allowed time. Your content was kept. You can retry or continue manually.", diag, isTimeout: true, category: "timeout");
        }
        catch (HttpRequestException)
        {
            sw.Stop();
            string diag = ResearchAiDiagnostics.Log("ConflictFixes", "Http", timeoutSeconds, sw.Elapsed.TotalSeconds, outcome: "network error", category: "network");
            throw new ResearchAiException("Could not reach the Research AI service. Check the endpoint in Settings and your connection.", diag, category: "network");
        }
    }

    private async Task<ExtractionSheetResult> PostExtractionSheetAsync(string path, ResearchProject project, object extra, CancellationToken cancellationToken)
    {
        var o = _options();
        if (string.IsNullOrWhiteSpace(o.EndpointBaseUrl))
            throw new ResearchAiNotConfiguredException();

        string url = o.EndpointBaseUrl.TrimEnd('/') + "/api/research-ai/" + path;

        var request = new
        {
            projectId = project.Id,
            taskType = path,
            project = ResearchAiProjectPayload.From(project),
            csvSampleSummary = project.CsvSampleSummary,   // safe summary only, never full data
            existingVariables = project.Variables,
            googleFormUrl = project.GoogleFormUrl,
            extra,
            desiredOutputFormat = "json"
        };

        int timeoutSeconds = ResearchAiTimeouts.Resolve(o.TimeoutSeconds);
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(request, Json), Encoding.UTF8, "application/json");
            using var httpResp = await Http.PostAsync(url, content, cts.Token);
            string body = await httpResp.Content.ReadAsStringAsync(cts.Token);
            sw.Stop();

            if (!httpResp.IsSuccessStatusCode)
            {
                string diag = ResearchAiDiagnostics.Log(path, "Http", timeoutSeconds, sw.Elapsed.TotalSeconds, (int)httpResp.StatusCode, string.IsNullOrEmpty(body));
                throw new ResearchAiException((int)httpResp.StatusCode switch
                {
                    504 or 408 => "The provider stopped the request before completion. Your content was kept. Try again with the compact input or split the task.",
                    500 => $"Research AI provider returned an internal error after {sw.Elapsed.TotalSeconds:F0} seconds. Your content was kept. You can retry with compact input or continue manually.",
                    502 or 503 => "The Research AI provider is overloaded or temporarily unavailable. Your content was kept. Try again in a moment.",
                    _ => $"The Research AI service returned an error ({(int)httpResp.StatusCode})."
                }, diag,
                isTimeout: (int)httpResp.StatusCode is 504 or 408,
                category: (int)httpResp.StatusCode switch
                {
                    504 or 408 => "timeout",
                    502 or 503 => "overload",
                    429 => "rate_limit",
                    401 or 403 => "auth",
                    >= 500 => "provider_error",
                    _ => "provider_error"
                });
            }

            if (!_parser.TryParseExtractionSheet(body, out var result) || !result.HasAnyContent)
            {
                string diag = ResearchAiDiagnostics.Log(path, "Http", timeoutSeconds, sw.Elapsed.TotalSeconds, (int)httpResp.StatusCode, string.IsNullOrEmpty(body), "could not parse extraction sheet JSON");
                throw new ResearchAiException("The Research AI response could not be read as an extraction sheet.", diag);
            }

            ResearchAiDiagnostics.Log(path, "Http", timeoutSeconds, sw.Elapsed.TotalSeconds, (int)httpResp.StatusCode, outcome: "success");
            result.SourceMode = ResearchSourceMode.AiGenerated;
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            string diag = ResearchAiDiagnostics.Log(path, "Http", timeoutSeconds, sw.Elapsed.TotalSeconds, outcome: "timeout");
            throw new ResearchAiException("Research AI did not finish within the allowed time. Your content was kept. You can retry or continue manually.", diag, isTimeout: true);
        }
        catch (HttpRequestException)
        {
            sw.Stop();
            string diag = ResearchAiDiagnostics.Log(path, "Http", timeoutSeconds, sw.Elapsed.TotalSeconds, outcome: "network error");
            throw new ResearchAiException("Could not reach the Research AI service. Check the endpoint in Settings and your connection.", diag);
        }
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

        int timeoutSeconds = ResearchAiTimeouts.Resolve(o.TimeoutSeconds);
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(request, Json), Encoding.UTF8, "application/json");
            using var httpResp = await Http.PostAsync(url, content, cts.Token);
            string body = await httpResp.Content.ReadAsStringAsync(cts.Token);
            sw.Stop();

            if (!httpResp.IsSuccessStatusCode)
            {
                string diag = ResearchAiDiagnostics.Log(path, "Http", timeoutSeconds, sw.Elapsed.TotalSeconds, (int)httpResp.StatusCode, string.IsNullOrEmpty(body));
                throw new ResearchAiException((int)httpResp.StatusCode switch
                {
                    504 or 408 => "The provider stopped the request before completion. Your content was kept. Try again with the compact input or split the task.",
                    500 => $"Research AI provider returned an internal error after {sw.Elapsed.TotalSeconds:F0} seconds. Your content was kept. You can retry with compact input or continue manually.",
                    502 or 503 => "The Research AI provider is overloaded or temporarily unavailable. Your content was kept. Try again in a moment.",
                    _ => $"The Research AI service returned an error ({(int)httpResp.StatusCode})."
                }, diag,
                isTimeout: (int)httpResp.StatusCode is 504 or 408,
                category: (int)httpResp.StatusCode switch
                {
                    504 or 408 => "timeout",
                    502 or 503 => "overload",
                    429 => "rate_limit",
                    401 or 403 => "auth",
                    >= 500 => "provider_error",
                    _ => "provider_error"
                });
            }

            ResearchAiResponse? parsed = null;
            string? parseErr = null;
            try { parsed = JsonSerializer.Deserialize<ResearchAiResponse>(body, Json); }
            catch (Exception pex) { parseErr = pex.GetType().Name; /* fall through to tolerant parsing */ }

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
            {
                string diag = ResearchAiDiagnostics.Log(path, "Http", timeoutSeconds, sw.Elapsed.TotalSeconds, (int)httpResp.StatusCode, string.IsNullOrEmpty(body), parseErr);
                throw new ResearchAiException(parsed.ErrorMessage, diag);
            }

            ResearchAiDiagnostics.Log(path, "Http", timeoutSeconds, sw.Elapsed.TotalSeconds, (int)httpResp.StatusCode, string.IsNullOrEmpty(body), parseErr, "success");
            return parsed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            string diag = ResearchAiDiagnostics.Log(path, "Http", timeoutSeconds, sw.Elapsed.TotalSeconds, outcome: "timeout");
            throw new ResearchAiException("Research AI did not finish within the allowed time. Your content was kept. You can retry or continue manually.", diag, isTimeout: true);
        }
        catch (HttpRequestException)
        {
            sw.Stop();
            string diag = ResearchAiDiagnostics.Log(path, "Http", timeoutSeconds, sw.Elapsed.TotalSeconds, outcome: "network error");
            throw new ResearchAiException("Could not reach the Research AI service. Check the endpoint in Settings and your connection.", diag);
        }
    }

    // ---- Health check (Part G) ---------------------------------------------
    // Sends a minimal request and maps the outcome to a small, user-facing
    // status. Never throws — always returns a result so Settings can show it.
    public async Task<ResearchAiHealthResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var o = _options();
        if (string.IsNullOrWhiteSpace(o.EndpointBaseUrl))
            return new ResearchAiHealthResult { Status = "Not configured", Message = "Add a backend endpoint URL first." };

        string url = o.EndpointBaseUrl.TrimEnd('/') + "/api/research-ai/health";
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(20));   // a ping should be fast; not the full generation floor

        try
        {
            using var httpResp = await Http.GetAsync(url, cts.Token);
            string body = await httpResp.Content.ReadAsStringAsync(cts.Token);
            sw.Stop();

            if (httpResp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return new ResearchAiHealthResult { Status = "Unauthorized", Message = "The endpoint rejected the request.", ElapsedSeconds = sw.Elapsed.TotalSeconds };
            if (!httpResp.IsSuccessStatusCode)
                return new ResearchAiHealthResult { Status = "Provider unavailable", Message = $"The endpoint returned an error ({(int)httpResp.StatusCode}).", ElapsedSeconds = sw.Elapsed.TotalSeconds };
            if (string.IsNullOrWhiteSpace(body))
                return new ResearchAiHealthResult { Status = "Empty response", Message = "The endpoint responded with no content.", ElapsedSeconds = sw.Elapsed.TotalSeconds };

            return new ResearchAiHealthResult { Success = true, Status = "Connected", Message = "The Research AI endpoint responded successfully.", ElapsedSeconds = sw.Elapsed.TotalSeconds };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ResearchAiHealthResult { Status = "Timed out", Message = "The endpoint did not respond in time.", ElapsedSeconds = sw.Elapsed.TotalSeconds };
        }
        catch (HttpRequestException)
        {
            return new ResearchAiHealthResult { Status = "Provider unavailable", Message = "Could not reach the endpoint. Check the URL and your connection.", ElapsedSeconds = sw.Elapsed.TotalSeconds };
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

    // Development mock cannot truly parse free text, so it never pretends to.
    // It keeps the pasted text verbatim in Background for review and flags every
    // structured section as needing manual completion. No fabrication.
    public async Task<ProposalExtractionResult> ExtractProposalAsync(string proposalText, ResearchProject existingProject, CancellationToken cancellationToken)
    {
        await Task.Delay(700, cancellationToken);

        string text = (proposalText ?? "").Trim();
        var result = new ProposalExtractionResult
        {
            SourceMode = ResearchSourceMode.DevelopmentMock,
            ConfidenceSummary =
                "Development mode: automatic extraction is not available offline. The pasted text " +
                "has been kept below for you to copy into the correct fields. Enable the connected " +
                "Research AI provider or a backend endpoint for real extraction.",
            Warnings =
            {
                "This is a development-mode placeholder — no fields were parsed from the text."
            },
            MissingOrWeakSections =
            {
                "All sections require manual review in development mode."
            }
        };
        result.ExtractedProposalSections.Background = text;
        return result;
    }

    // Phase 3 — development mock. Builds a small scaffold ONLY from the project's
    // own fields, its existing recommendation variables, and any CSV column
    // headers. It never fabricates data, statistics, or a sample size, and it is
    // clearly labelled as a development draft.
    public async Task<ExtractionSheetResult> GenerateExtractionSheetAsync(ResearchProject project, string extraQuestionsContext, CancellationToken cancellationToken)
    {
        await Task.Delay(700, cancellationToken);

        var result = new ExtractionSheetResult
        {
            SourceMode = ResearchSourceMode.DevelopmentMock,
            ConfidenceSummary = "Development mode: this is a basic scaffold from your project fields — review and complete every row.",
            Warnings = { "Development-mode placeholder — no real AI extraction was performed." }
        };

        // Reuse any recommendation variables the project already has.
        if (project.Recommendations is { SuggestedVariables.Count: > 0 })
            foreach (var v in project.Recommendations.SuggestedVariables)
                result.Variables.Add(new ResearchVariable
                {
                    VariableName = Slug(v.HeaderDisplay),
                    QuestionLabel = v.VariableLabel,
                    VariableType = MapType(v.VariableType),
                    Role = MapRole(v.Role),
                    Coding = v.SuggestedCoding,
                    Source = "AI Recommendation",
                    Notes = v.Notes
                });

        // Map CSV headers to rows if a sample was uploaded.
        if (project.CsvSampleSummary is { Columns.Count: > 0 })
            foreach (var c in project.CsvSampleSummary.Columns)
            {
                if (result.Variables.Any(x => string.Equals(x.VariableName, c.Name, StringComparison.OrdinalIgnoreCase))) continue;
                result.Variables.Add(new ResearchVariable
                {
                    VariableName = c.Name,
                    VariableType = MapType(c.InferredType),
                    Role = "Unknown",
                    Source = "CSV Sample",
                    Notes = c.IsLikelyCategorical ? "Add value labels for each category." : ""
                });
            }

        // Minimal always-useful demographics if nothing else was produced.
        if (result.Variables.Count == 0)
        {
            result.Variables.Add(new ResearchVariable { VariableName = "participant_id", VariableType = "ID", Role = "Identifier", Source = "Manual", IsRequired = true, Notes = "One anonymised ID per record." });
            result.Variables.Add(new ResearchVariable { VariableName = "age", VariableType = "Continuous", MeasurementLevel = "Scale", Role = "Demographic", Source = "Manual" });
            result.Variables.Add(new ResearchVariable { VariableName = "sex", VariableType = "Categorical", MeasurementLevel = "Nominal", Role = "Demographic", Coding = "0 = Male, 1 = Female", Source = "Manual" });
            result.Variables.Add(new ResearchVariable { VariableName = "primary_outcome", VariableType = "Unknown", Role = "Outcome", Source = "Manual", IsRequired = true, Notes = "Define your actual primary outcome." });
        }

        result.MissingExpectedVariables.Add("Confirm your primary outcome variable is defined and marked.");
        return result;
    }

    public async Task<ExtractionSheetResult> ExtractVariablesFromQuestionsAsync(ResearchProject project, string questionsText, CancellationToken cancellationToken)
    {
        await Task.Delay(500, cancellationToken);

        var result = new ExtractionSheetResult
        {
            SourceMode = ResearchSourceMode.DevelopmentMock,
            ConfidenceSummary = "Development mode: each question was turned into a draft variable — set types, roles, and coding yourself.",
            Warnings = { "Development-mode placeholder — no real AI extraction was performed." }
        };

        foreach (var raw in (questionsText ?? "").Split('\n'))
        {
            string q = raw.Trim().TrimStart('-', '*', '•', '·').Trim();
            if (q.Length < 3) continue;
            // Drop a leading "1." / "Q3)" style numbering for the label.
            string label = System.Text.RegularExpressions.Regex.Replace(q, @"^\s*(Q?\d+[\.\)\:]\s*)", "").Trim();
            result.Variables.Add(new ResearchVariable
            {
                VariableName = Slug(label),
                QuestionLabel = label,
                VariableType = "Unknown",
                Role = "Unknown",
                Source = "Questionnaire",
                Notes = "Set the type, role, and coding for this question."
            });
        }

        if (result.Variables.Count == 0)
            result.Warnings.Add("No questions were detected. Put one question per line and try again.");

        return result;
    }

    public async Task<ExtractionSheetResult> SuggestExtractionFixesAsync(ResearchProject project, ExtractionValidationReport report, CancellationToken cancellationToken)
    {
        await Task.Delay(500, cancellationToken);

        var result = new ExtractionSheetResult
        {
            SourceMode = ResearchSourceMode.DevelopmentMock,
            ConfidenceSummary = "Development mode: only safe, mechanical fixes were applied — review each change.",
            Warnings = { "Development-mode placeholder — connect the Research AI provider for full fixes." }
        };

        foreach (var v in project.Variables)
        {
            var copy = v.Clone();
            copy.Id = v.Id;   // keep identity so Apply updates in place
            if (string.IsNullOrWhiteSpace(copy.VariableType)) { copy.VariableType = "Unknown"; result.ChangeSummary.Add($"Set a type placeholder for '{copy.VariableName}'."); }
            if (string.IsNullOrWhiteSpace(copy.Role)) { copy.Role = "Unknown"; result.ChangeSummary.Add($"Set a role placeholder for '{copy.VariableName}'."); }
            if (string.IsNullOrWhiteSpace(copy.VariableName)) { copy.Notes = ("Needs a variable name. " + copy.Notes).Trim(); result.ChangeSummary.Add("Flagged a row with no variable name."); }
            result.Variables.Add(copy);
        }

        if (result.ChangeSummary.Count == 0)
            result.ChangeSummary.Add("No automatic fixes were needed in development mode.");

        return result;
    }

    public async Task<ConflictFixResult> SuggestConflictFixesAsync(ResearchProject project, List<ConflictFixInput> conflicts, CancellationToken cancellationToken)
    {
        await Task.Delay(400, cancellationToken);

        // Deterministic offline proposals so the review/apply UI can be exercised
        // without a provider: CSV-only columns become add-variable proposals,
        // sheet-only variables get an alias suggestion, everything else is
        // flagged for manual review. Clearly marked as development output.
        var result = new ConflictFixResult
        {
            Warnings = { "Development-mode placeholder — connect the Research AI provider for real fix proposals." }
        };
        // Google Form / system metadata columns are routine safe-ignores.
        var meta = new[] { "timestamp", "username", "email address", "email", "score" };
        foreach (var c in conflicts ?? new List<ConflictFixInput>())
        {
            string colName = c.ColumnName ?? "";
            string varName = c.VariableName ?? "";
            bool isMeta = c.Kind == "CsvOnly" && meta.Contains(colName.Trim().ToLowerInvariant());
            result.Fixes.Add(c.Kind switch
            {
                // Sample-size / outcome / required problems are never auto-handled.
                "SampleIncomplete" or "Outcome" => new ConflictFixProposal
                {
                    ConflictKey = c.ConflictKey, ConflictTitle = c.Title, Category = "manual_review", Action = "no_safe_fix",
                    Explanation = "Development mock: this may affect statistics — review it manually.",
                    Confidence = "high", TargetVariable = varName
                },
                "CsvOnly" when isMeta => new ConflictFixProposal
                {
                    ConflictKey = c.ConflictKey, ConflictTitle = c.Title, Category = "safe_ignore", Action = "ignore",
                    Explanation = "Development mock: Google Form system column — safe to ignore for analysis.",
                    Confidence = "high", TargetColumn = colName
                },
                "CsvOnly" => new ConflictFixProposal
                {
                    ConflictKey = c.ConflictKey, ConflictTitle = c.Title, Category = "safe_fix", Action = "add_variable",
                    Explanation = "Development mock: add this CSV column as a new variable.",
                    Confidence = "medium", TargetColumn = colName,
                    ProposedValue = colName
                },
                "SheetOnly" => new ConflictFixProposal
                {
                    ConflictKey = c.ConflictKey, ConflictTitle = c.Title, Category = "safe_fix", Action = "mark_resolved",
                    Explanation = "Development mock: keep the variable; its data may come later.",
                    Confidence = "low", TargetVariable = varName
                },
                _ => new ConflictFixProposal
                {
                    ConflictKey = c.ConflictKey, ConflictTitle = c.Title, Category = "manual_review", Action = "no_safe_fix",
                    Explanation = "Development mock: review this conflict manually.",
                    Confidence = "low", TargetVariable = varName, TargetColumn = colName
                }
            });
        }
        return result;
    }

    public async Task<ResearchAiHealthResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(200, cancellationToken);
        return new ResearchAiHealthResult { Success = true, Status = "Connected", Message = "Development mock is active (offline, no real provider called).", ElapsedSeconds = 0.2 };
    }

    private static string Slug(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "variable";
        string cleaned = System.Text.RegularExpressions.Regex.Replace(s.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
        if (cleaned.Length > 32) cleaned = cleaned.Substring(0, 32).Trim('_');
        return cleaned.Length == 0 ? "variable" : cleaned;
    }

    private static string MapType(string t)
    {
        string s = (t ?? "").ToLowerInvariant();
        if (s.Contains("continu") || s.Contains("scale")) return "Continuous";
        if (s.Contains("binary")) return "Binary";
        if (s.Contains("ordin")) return "Ordinal";
        if (s.Contains("categor") || s.Contains("nominal")) return "Categorical";
        if (s.Contains("numeric") || s.Contains("number") || s.Contains("int")) return "Numeric";
        if (s.Contains("date") || s.Contains("time")) return "Date";
        if (s.Contains("id")) return "ID";
        if (s.Contains("text") || s.Contains("string")) return "Text";
        return "Unknown";
    }

    private static string MapRole(string r)
    {
        string s = (r ?? "").ToLowerInvariant();
        if (s.Contains("outcome") || s.Contains("dependent")) return "Outcome";
        if (s.Contains("exposure")) return "Exposure";
        if (s.Contains("predict") || s.Contains("independent")) return "Predictor";
        if (s.Contains("confound")) return "Confounder";
        if (s.Contains("demo")) return "Demographic";
        if (s.Contains("ident") || s.Contains("id")) return "Identifier";
        if (s.Contains("eligib") || s.Contains("inclusion") || s.Contains("exclusion")) return "Eligibility";
        return "Unknown";
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
        string text = await CompleteAsync("Recommendations", RecommendationsSystemPrompt, BuildRecommendationsUserPrompt(project), cancellationToken, maxOutputTokens: 6000);

        if (!_parser.TryParseRecommendations(text, out var rec) || !rec.HasStructuredContent)
        {
            string diag = ResearchAiDiagnostics.Log("Recommendations", "DevZai", ResearchAiTimeouts.DefaultLongRunningSeconds, 0, parseError: "could not parse recommendations JSON", category: "parse_failure");
            throw new ResearchAiException("The Research AI response could not be read as recommendations.", diag, category: "parse_failure");
        }

        rec.SourceMode = ResearchSourceMode.AiGenerated;
        return rec;
    }

    public async Task<ResearchProposalDraft> GenerateProposalDraftAsync(ResearchProject project, ResearchRecommendations? recommendations, CancellationToken cancellationToken)
    {
        string text = await CompleteAsync("ProposalDraft", ProposalSystemPrompt, BuildProposalUserPrompt(project, recommendations), cancellationToken);

        if (!_parser.TryParseProposal(text, out var draft))
        {
            string diag = ResearchAiDiagnostics.Log("ProposalDraft", "DevZai", ResearchAiTimeouts.DefaultLongRunningSeconds, 0, parseError: "could not parse proposal JSON", category: "parse_failure");
            throw new ResearchAiException("The Research AI response could not be read as a proposal draft.", diag, category: "parse_failure");
        }

        draft.SourceMode = ResearchSourceMode.AiGenerated;
        draft.IsTemplateGenerated = false;
        return draft;
    }

    public async Task<ProposalExtractionResult> ExtractProposalAsync(string proposalText, ResearchProject existingProject, CancellationToken cancellationToken)
    {
        // Same working dev-provider pattern as recommendations/proposal: 180s
        // timeout floor, max_tokens 8000, strict-JSON prompt, tolerant parser.
        // Extraction measured ~100-150s in testing, comfortably within 180s.
        string text = await CompleteAsync("ExtractProposal", ExtractionSystemPrompt, BuildExtractionUserPrompt(proposalText, existingProject), cancellationToken);

        if (!_parser.TryParseExtraction(text, out var result) || !result.HasAnyContent)
        {
            string diag = ResearchAiDiagnostics.Log("ExtractProposal", "DevZai", ResearchAiTimeouts.DefaultLongRunningSeconds, 0, parseError: "could not parse extraction JSON");
            throw new ResearchAiException("The Research AI response could not be read as extracted proposal details. Please try again.", diag);
        }

        result.SourceMode = ResearchSourceMode.AiGenerated;
        return result;
    }

    // ---- Phase 3: Data Extraction Sheet (variable sheet only, no statistics) --

    public async Task<ExtractionSheetResult> GenerateExtractionSheetAsync(ResearchProject project, string extraQuestionsContext, CancellationToken cancellationToken)
    {
        // Variables-only output with a tight token cap: the old combination of a
        // full-proposal prompt + one huge max_tokens made the provider generate
        // for minutes and sometimes die with a 500 mid-way. Compact in, compact out.
        string text = await CompleteAsync("ExtractionSheet", ExtractionSheetSystemPrompt, BuildExtractionSheetUserPrompt(project, extraQuestionsContext), cancellationToken, maxOutputTokens: 4000);
        if (!_parser.TryParseExtractionSheet(text, out var result))
        {
            string diag = ResearchAiDiagnostics.Log("ExtractionSheet", "DevZai", ResearchAiTimeouts.DefaultLongRunningSeconds, 0, parseError: "could not parse extraction sheet JSON", category: "parse_failure");
            throw new ResearchAiException("The Research AI response could not be read as an extraction sheet. Please try again.", diag, category: "parse_failure");
        }
        // A sheet with zero variables is a failure, never a silent "success":
        // the caller must be able to show a real message and offer Retry.
        if (result.Variables.Count == 0)
        {
            string diag = ResearchAiDiagnostics.Log("ExtractionSheet", "DevZai", ResearchAiTimeouts.DefaultLongRunningSeconds, 0, parseError: "extraction sheet had no variables", category: "parse_failure");
            throw new ResearchAiException("Research AI returned an incomplete extraction sheet. Please retry or continue manually.", diag, category: "parse_failure");
        }
        result.SourceMode = ResearchSourceMode.AiGenerated;
        return result;
    }

    // Above this many question lines, a single request risks an oversized/slow
    // call — split into batches instead so quality (per-question extraction
    // accuracy) is preserved while no single request has to carry everything.
    private const int QuestionBatchLineThreshold = 25;
    private const int QuestionBatchSize = 20;

    public async Task<ExtractionSheetResult> ExtractVariablesFromQuestionsAsync(ResearchProject project, string questionsText, CancellationToken cancellationToken)
    {
        questionsText ??= "";
        var lines = questionsText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        if (lines.Count <= QuestionBatchLineThreshold)
            return await ExtractVariablesFromQuestionsBatchAsync(project, questionsText, cancellationToken);

        // Batch: same strict-JSON extraction per chunk, merged and deduped by
        // normalized variable name afterward. No quality loss — every question
        // still goes through the identical per-question extraction prompt; only
        // the request size is capped so it stays fast and reliable.
        var merged = new ExtractionSheetResult { SourceMode = ResearchSourceMode.AiGenerated };
        var seenNames = new HashSet<string>();
        int batchCount = (int)Math.Ceiling(lines.Count / (double)QuestionBatchSize);

        for (int b = 0; b < batchCount; b++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchLines = lines.Skip(b * QuestionBatchSize).Take(QuestionBatchSize).ToList();
            string batchText = string.Join("\n", batchLines);
            string text = await CompleteAsync("QuestionsToVariables", QuestionsToVariablesSystemPrompt, BuildQuestionsUserPrompt(project, batchText), cancellationToken, maxOutputTokens: 4000);
            if (!_parser.TryParseExtractionSheet(text, out var batchResult)) continue;

            foreach (var v in batchResult.Variables)
            {
                string key = NormalizeVarKey(v.VariableName);
                if (key.Length > 0 && !seenNames.Add(key)) continue;   // dedupe across batches
                merged.Variables.Add(v);
            }
            merged.Warnings.AddRange(batchResult.Warnings);
            merged.MissingExpectedVariables.AddRange(batchResult.MissingExpectedVariables);
        }

        if (!merged.HasAnyContent)
        {
            string diag = ResearchAiDiagnostics.Log("QuestionsToVariables", "DevZai", ResearchAiTimeouts.DefaultLongRunningSeconds, 0, parseError: "batched extraction produced no variables");
            throw new ResearchAiException("The Research AI response could not be read as variable suggestions. Please try again.", diag);
        }
        merged.ConfidenceSummary = $"Built from {batchCount} batches of questions ({lines.Count} total lines).";
        return merged;
    }

    private static string NormalizeVarKey(string s)
        => string.IsNullOrWhiteSpace(s) ? "" : System.Text.RegularExpressions.Regex.Replace(s.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');

    private async Task<ExtractionSheetResult> ExtractVariablesFromQuestionsBatchAsync(ResearchProject project, string questionsText, CancellationToken cancellationToken)
    {
        string text = await CompleteAsync("QuestionsToVariables", QuestionsToVariablesSystemPrompt, BuildQuestionsUserPrompt(project, questionsText), cancellationToken, maxOutputTokens: 4000);
        if (!_parser.TryParseExtractionSheet(text, out var result) || !result.HasAnyContent)
        {
            string diag = ResearchAiDiagnostics.Log("QuestionsToVariables", "DevZai", ResearchAiTimeouts.DefaultLongRunningSeconds, 0, parseError: "could not parse variable suggestions JSON", category: "parse_failure");
            throw new ResearchAiException("The Research AI response could not be read as variable suggestions. Please try again.", diag, category: "parse_failure");
        }
        result.SourceMode = ResearchSourceMode.AiGenerated;
        return result;
    }

    public async Task<ExtractionSheetResult> SuggestExtractionFixesAsync(ResearchProject project, ExtractionValidationReport report, CancellationToken cancellationToken)
    {
        string text = await CompleteAsync("SuggestFixes", ExtractionFixSystemPrompt, BuildFixUserPrompt(project, report), cancellationToken, maxOutputTokens: 4000);
        if (!_parser.TryParseExtractionSheet(text, out var result) || !result.HasAnyContent)
        {
            string diag = ResearchAiDiagnostics.Log("SuggestFixes", "DevZai", ResearchAiTimeouts.DefaultLongRunningSeconds, 0, parseError: "could not parse suggested fixes JSON", category: "parse_failure");
            throw new ResearchAiException("The Research AI response could not be read as suggested fixes. Please try again.", diag, category: "parse_failure");
        }
        result.SourceMode = ResearchSourceMode.AiGenerated;
        return result;
    }

    // Above this many conflicts, prioritize (errors first) and cap the request —
    // one fix proposal per conflict keeps the output small and reliable.
    private const int ConflictFixMaxConflicts = 25;

    public async Task<ConflictFixResult> SuggestConflictFixesAsync(ResearchProject project, List<ConflictFixInput> conflicts, CancellationToken cancellationToken)
    {
        conflicts ??= new List<ConflictFixInput>();
        // Prioritize serious conflicts; cap the batch so the prompt stays small.
        var prioritized = conflicts
            .OrderBy(c => c.Severity == "Error" ? 0 : c.Severity == "Warning" ? 1 : 2)
            .Take(ConflictFixMaxConflicts)
            .ToList();

        string text = await CompleteAsync("ConflictFixes", ConflictFixesSystemPrompt, BuildConflictFixesUserPrompt(project, prioritized), cancellationToken, maxOutputTokens: 3000);
        if (!_parser.TryParseConflictFixes(text, out var result) || result.Fixes.Count == 0)
        {
            string diag = ResearchAiDiagnostics.Log("ConflictFixes", "DevZai", ResearchAiTimeouts.DefaultLongRunningSeconds, 0, parseError: "could not parse conflict fixes JSON", category: "parse_failure");
            throw new ResearchAiException("The Research AI response could not be read as conflict fixes. Please try again.", diag, category: "parse_failure");
        }
        if (conflicts.Count > prioritized.Count)
            result.Warnings.Add($"{conflicts.Count - prioritized.Count} lower-priority conflict(s) were not sent in this batch — run Fix with Research AI again after applying these fixes.");
        return result;
    }

    // Minimal health-check ping (Part G): tiny prompt, short timeout, small
    // max_tokens — never consumes meaningful quota. Never throws.
    public async Task<ResearchAiHealthResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var creds = _credentials();
        if (!creds.HasKey)
            return new ResearchAiHealthResult { Status = "Not configured", Message = "No AI provider key is available. Add your API key in AI Settings." };

        string baseUrl = string.IsNullOrWhiteSpace(creds.BaseUrl) ? "https://api.z.ai/api/paas/v4" : creds.BaseUrl.Trim().TrimEnd('/');
        string model = string.IsNullOrWhiteSpace(creds.Model) ? "GLM-4.7-FlashX" : creds.Model.Trim();

        var body = new
        {
            model,
            messages = new[] { new { role = "user", content = "Reply with the single word: OK" } },
            temperature = 0,
            max_tokens = 8,
            stream = false
        };

        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(20));   // a ping should be fast; not the full generation floor

        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.ApiKey.Trim());
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        try
        {
            var response = await Http.SendAsync(request, cts.Token);
            string json = await response.Content.ReadAsStringAsync(cts.Token);
            sw.Stop();

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return new ResearchAiHealthResult { Status = "Unauthorized", Message = "The provider rejected the request. Check your API key in AI Settings.", ElapsedSeconds = sw.Elapsed.TotalSeconds };
            if (!response.IsSuccessStatusCode)
                return new ResearchAiHealthResult { Status = "Provider unavailable", Message = $"The provider returned an error ({(int)response.StatusCode}).", ElapsedSeconds = sw.Elapsed.TotalSeconds };

            string content = ExtractContent(json);
            if (string.IsNullOrWhiteSpace(content))
                return new ResearchAiHealthResult { Status = "Empty response", Message = "The provider responded with no content.", ElapsedSeconds = sw.Elapsed.TotalSeconds };

            return new ResearchAiHealthResult { Success = true, Status = "Connected", Message = "The Research AI provider responded successfully.", ElapsedSeconds = sw.Elapsed.TotalSeconds };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ResearchAiHealthResult { Status = "Timed out", Message = "The provider did not respond in time.", ElapsedSeconds = sw.Elapsed.TotalSeconds };
        }
        catch (HttpRequestException)
        {
            return new ResearchAiHealthResult { Status = "Provider unavailable", Message = "Could not reach the provider. Check your connection.", ElapsedSeconds = sw.Elapsed.TotalSeconds };
        }
        catch (Exception)
        {
            return new ResearchAiHealthResult { Status = "Parse error", Message = "The response could not be read.", ElapsedSeconds = sw.Elapsed.TotalSeconds };
        }
    }

    // maxOutputTokens is per-action: variable lists and conflict fixes need far
    // fewer output tokens than a full proposal extraction. A smaller cap keeps
    // the provider's generation shorter and materially reduces mid-generation
    // provider 500s on long outputs. Never one huge setting for every action.
    private async Task<string> CompleteAsync(string action, string systemPrompt, string userPrompt, CancellationToken cancellationToken,
        int minTimeoutSeconds = ResearchAiTimeouts.DefaultLongRunningSeconds, int maxOutputTokens = 8000)
    {
        var creds = _credentials();
        if (!creds.HasKey)
            throw new ResearchAiException("No AI provider key is available. Add your API key in AI Settings, then try again.", category: "auth");

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
            max_tokens = maxOutputTokens,
            stream = false
        };

        // Complex research tasks can legitimately take many minutes — never let a
        // small configured timeout cut off a genuine long-running request. Floors
        // at minTimeoutSeconds (15 minutes by default); the student may raise the
        // configured value up to the 45-minute maximum. Centralized via
        // ResearchAiTimeouts so every call path (dev provider + backend HTTP
        // client) applies the exact same floor/ceiling.
        int timeoutSeconds = ResearchAiTimeouts.Resolve(opts.TimeoutSeconds, minTimeoutSeconds);
        int inputChars = systemPrompt.Length + userPrompt.Length;
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        // Safe diagnostics helper for this call — sizes/status only, never content.
        string LogCall(string? outcome = null, int? status = null, bool empty = false, string? parseErr = null, string? category = null)
            => ResearchAiDiagnostics.Log(action, "DevZai", timeoutSeconds, sw.Elapsed.TotalSeconds, status, empty, parseErr, outcome,
                model, inputChars, maxOutputTokens, category);

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
            sw.Stop();
            throw new ResearchAiException("Research AI did not finish within the allowed time. Your content was kept. You can retry or continue manually.",
                LogCall(outcome: "timeout", category: "timeout"), isTimeout: true, category: "timeout");
        }
        catch (HttpRequestException)
        {
            sw.Stop();
            throw new ResearchAiException("Could not reach the Research AI service. Check your connection and try again.",
                LogCall(outcome: "network error", category: "network"), category: "network");
        }
        sw.Stop();

        if (!response.IsSuccessStatusCode)
        {
            int code = (int)response.StatusCode;
            // Map statuses to friendly, key-free messages AND a machine-readable
            // category so the UI never treats a provider failure as "timeout".
            // The raw provider body is intentionally NOT included (it can echo
            // request details).
            (string msg, string category, bool isTimeout) = code switch
            {
                504 or 408 => ("The provider stopped the request before completion. Your content was kept. Try again with the compact input or split the task.", "timeout", true),
                500 => ($"Research AI provider returned an internal error after {sw.Elapsed.TotalSeconds:F0} seconds. Your content was kept. You can retry with compact input or continue manually.", "provider_error", false),
                502 or 503 => ("The Research AI provider is overloaded or temporarily unavailable. Your content was kept. Try again in a moment.", "overload", false),
                429 => ("The Research AI provider is busy (rate limited). Wait a moment and try again.", "rate_limit", false),
                413 => ("The request was too large for the provider. Your content was kept. Retry sends compact input; if it persists, split the task.", "prompt_too_long", false),
                401 or 403 => ("The Research AI provider rejected the request. Check your API key in AI Settings.", "auth", false),
                >= 500 => ("The Research AI provider had a server error. Your content was kept. Please try again shortly.", "provider_error", false),
                _ => ("The Research AI provider returned an error. Your content was kept. Please try again.", "provider_error", false)
            };
            throw new ResearchAiException(msg, LogCall(status: code, category: category), isTimeout, category);
        }

        string content = ExtractContent(json);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ResearchAiException("The Research AI service returned an empty response. Your content was kept. Please try again.",
                LogCall(status: (int)response.StatusCode, empty: true, category: "empty_response"), category: "empty_response");
        }

        LogCall(outcome: "success", status: (int)response.StatusCode);
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

    // Phase 2F — extraction. Reads structured fields out of an EXISTING proposal
    // the student supplied. This is extraction, not authoring: every value must
    // come from the supplied text, and anything not present is reported as
    // missing/unclear rather than invented.
    private const string ExtractionSystemPrompt =
        "You are a careful research assistant. The student has pasted an EXISTING " +
        "research proposal (or a draft). Your job is ONLY to EXTRACT information " +
        "that is actually present in the supplied text and organise it into the " +
        "JSON shape below.\n\n" +
        "Strict extraction rules you must follow:\n" +
        "- Extract ONLY from the supplied proposal text. Do NOT add, invent, or " +
        "complete anything that is not clearly present.\n" +
        "- Do NOT fabricate methods, results, numbers, p-values, sample sizes, " +
        "statistics, references, or citations.\n" +
        "- If a field is not present in the text, return an empty string or empty " +
        "array for it, and list it in \"missingOrWeakSections\".\n" +
        "- If a field is present but vague or unclear, still extract what is there " +
        "and add a note to \"warnings\".\n" +
        "- Do NOT copy any Results or Discussion findings into the plan fields; " +
        "this tool is for the proposal/protocol only.\n" +
        "- \"confidenceSummary\" is a short, plain-language note on how complete the " +
        "proposal appears and what the student should double-check.\n" +
        "- Return ONLY one valid JSON object — no markdown, no code fences, no " +
        "commentary before or after the JSON.\n\n" +
        "Use exactly this JSON shape (include every field; use \"\" or [] where a " +
        "value is genuinely not present in the text):\n" +
        "{\n" +
        "  \"title\": \"string\",\n" +
        "  \"specialty\": \"string\",\n" +
        "  \"studyDesign\": \"string\",\n" +
        "  \"researchQuestion\": \"string\",\n" +
        "  \"aim\": \"string\",\n" +
        "  \"primaryObjective\": \"string\",\n" +
        "  \"secondaryObjectives\": [\"string\"],\n" +
        "  \"population\": \"string\",\n" +
        "  \"setting\": \"string\",\n" +
        "  \"timePeriod\": \"string\",\n" +
        "  \"inclusionCriteria\": [\"string\"],\n" +
        "  \"exclusionCriteria\": [\"string\"],\n" +
        "  \"variables\": [{\"name\":\"string\",\"label\":\"string\",\"type\":\"string\",\"role\":\"string\",\"coding\":\"string\",\"notes\":\"string\"}],\n" +
        "  \"suggestedAnalyses\": [{\"name\":\"string\",\"whenToUse\":\"string\",\"variablesNeeded\":\"string\",\"outputExpected\":\"string\",\"notes\":\"string\"}],\n" +
        "  \"dataCollection\": [\"string\"],\n" +
        "  \"ethics\": [\"string\"],\n" +
        "  \"limitations\": [\"string\"],\n" +
        "  \"timeline\": \"string\",\n" +
        "  \"proposalSections\": {\n" +
        "    \"background\": \"string\",\n" +
        "    \"rationale\": \"string\",\n" +
        "    \"aim\": \"string\",\n" +
        "    \"objectives\": \"string\",\n" +
        "    \"methods\": \"string\",\n" +
        "    \"studyDesign\": \"string\",\n" +
        "    \"setting\": \"string\",\n" +
        "    \"population\": \"string\",\n" +
        "    \"inclusionCriteria\": \"string\",\n" +
        "    \"exclusionCriteria\": \"string\",\n" +
        "    \"variables\": \"string\",\n" +
        "    \"dataCollection\": \"string\",\n" +
        "    \"statisticalAnalysisPlan\": \"string\",\n" +
        "    \"ethics\": \"string\",\n" +
        "    \"timeline\": \"string\",\n" +
        "    \"limitations\": \"string\"\n" +
        "  },\n" +
        "  \"missingOrWeakSections\": [\"string\"],\n" +
        "  \"warnings\": [\"string\"],\n" +
        "  \"confidenceSummary\": \"string\"\n" +
        "}\n\n" +
        "Return ONLY the JSON object above, filled in from the supplied text. No " +
        "markdown, no code fences, no explanation, and no text before or after the JSON.";

    // ---- Phase 3 prompts: extraction sheet / data dictionary ONLY -------------
    // These must produce a variable plan, never statistics or results.

    private const string ExtractionSheetSafetyRules =
        "Safety rules you must follow strictly:\n" +
        "- You are designing a DATA DICTIONARY / variable sheet ONLY. Do NOT " +
        "compute or report any statistics, p-values, test results, effect sizes, " +
        "or sample-size calculations.\n" +
        "- Do NOT invent data values, results, or a sample size. If the proposal " +
        "states a target sample size, you may echo it as text in \"targetSampleSize\"; " +
        "otherwise leave it empty.\n" +
        "- Base variables on the project, the imported proposal, the accepted plan, " +
        "the CSV column summary, and any questionnaire/Google Form questions " +
        "provided. Do NOT invent variables that none of these support.\n" +
        "- Use only these values. type: Text, Numeric, Binary, Categorical, " +
        "Ordinal, Continuous, Date, ID, Unknown. measurementLevel: Nominal, " +
        "Ordinal, Scale, NotApplicable. role: Outcome, Exposure, Predictor, " +
        "Confounder, Demographic, Identifier, Eligibility, Other, Unknown. source: " +
        "Manual, AI Recommendation, Imported Proposal, Questionnaire, Google Form, " +
        "CSV Sample, Dataset. If unsure, use Unknown (or NotApplicable for level).\n" +
        "- variableName should be a short snake_case machine name; questionLabel is " +
        "the human/question wording. For categorical/binary/ordinal variables, put " +
        "the category coding in \"coding\"/\"valueLabels\".\n" +
        "- Return ONLY one valid JSON object — no markdown, no code fences, no " +
        "commentary before or after the JSON.";

    private const string ExtractionSheetJsonShape =
        "Use exactly this JSON shape (include every field; use \"\", [] or false " +
        "where a value genuinely does not apply):\n" +
        "{\n" +
        "  \"variables\": [{\n" +
        "    \"variableName\": \"string\",\n" +
        "    \"questionLabel\": \"string\",\n" +
        "    \"type\": \"string\",\n" +
        "    \"measurementLevel\": \"string\",\n" +
        "    \"role\": \"string\",\n" +
        "    \"coding\": \"string\",\n" +
        "    \"valueLabels\": \"string\",\n" +
        "    \"missingRule\": \"string\",\n" +
        "    \"source\": \"string\",\n" +
        "    \"required\": false,\n" +
        "    \"notes\": \"string\"\n" +
        "  }],\n" +
        "  \"missingExpectedVariables\": [\"string\"],\n" +
        "  \"extraOrUnexplainedColumns\": [\"string\"],\n" +
        "  \"changeSummary\": [\"string\"],\n" +
        "  \"warnings\": [\"string\"],\n" +
        "  \"targetSampleSize\": \"string\",\n" +
        "  \"confidenceSummary\": \"string\"\n" +
        "}\n\n" +
        "Return ONLY the JSON object above. No markdown, no code fences, no text " +
        "before or after the JSON.";

    private const string ExtractionSheetSystemPrompt =
        "You are a research methodology assistant helping a medical/university " +
        "student build a professional DATA EXTRACTION SHEET (data dictionary) for " +
        "a study that has NOT yet been run.\n\n" + ExtractionSheetSafetyRules + "\n\n" +
        "Produce a complete, non-overlapping list of variables the student will " +
        "need to collect: identifiers, demographics, exposure/predictor variables, " +
        "the primary and any secondary outcome variables, confounders, and " +
        "eligibility variables. Fill \"missingExpectedVariables\" with concepts the " +
        "proposal/objectives imply but that are not yet represented, and " +
        "\"extraOrUnexplainedColumns\" with CSV columns that do not map to a planned " +
        "variable. Leave \"changeSummary\" empty for a fresh generation.\n\n" +
        ExtractionSheetJsonShape;

    private const string QuestionsToVariablesSystemPrompt =
        "You convert questionnaire / Google Form QUESTIONS into DATA DICTIONARY " +
        "variables for a study that has NOT yet been run.\n\n" + ExtractionSheetSafetyRules + "\n\n" +
        "Create one variable per question (merge obvious duplicates). Put the " +
        "question wording in \"questionLabel\" and a short snake_case name in " +
        "\"variableName\". Infer a sensible type/level/role only where the question " +
        "clearly implies it; otherwise use Unknown. Set source to \"Questionnaire\" " +
        "(or \"Google Form\" if told the questions came from a form). Leave " +
        "\"changeSummary\" empty.\n\n" + ExtractionSheetJsonShape;

    private const string ExtractionFixSystemPrompt =
        "You are reviewing and CORRECTING an existing data extraction sheet (data " +
        "dictionary) for a study that has NOT yet been run.\n\n" + ExtractionSheetSafetyRules + "\n\n" +
        "You are given the current variables and a validation report. Return the " +
        "FULL corrected variable list (keep good rows unchanged, fix problematic " +
        "ones: rename invalid names, set missing type/level/role, add value labels " +
        "for categorical/binary/ordinal variables, add missing variables the " +
        "proposal implies, mark unclear items as Unknown, and add short notes for " +
        "anything that needs a human decision). Do NOT delete the student's data or " +
        "invent results. List every change you made in \"changeSummary\".\n\n" +
        ExtractionSheetJsonShape;

    // Structured per-conflict fixes: strict JSON, one proposal per conflict,
    // never a rewritten sheet, never fabricated data.
    private const string ConflictFixesSystemPrompt =
        "You are resolving VALIDATION CONFLICTS in a data extraction sheet (data " +
        "dictionary) for a study that has NOT yet been run.\n\n" + ExtractionSheetSafetyRules + "\n\n" +
        "You are given the sheet's variables, the uploaded CSV column HEADERS, and " +
        "a list of active conflicts. For EVERY conflict, propose exactly ONE fix. " +
        "Echo each conflict's conflictKey EXACTLY as given. Allowed actions:\n" +
        "- add_variable: a CSV column has no variable — proposedValue = new snake_case variable name, targetColumn = the CSV column.\n" +
        "- map_csv_column_to_variable: a CSV column belongs to an existing variable — targetVariable = that variable, targetColumn = the column, proposedValue = the column header to store as an alias.\n" +
        "- rename_variable: an invalid/duplicate variable name — targetVariable = current name, proposedValue = new snake_case name.\n" +
        "- add_alias: like map, when the variable should keep its name but match the column — proposedValue = alias text.\n" +
        "- update_coding: a categorical/binary/ordinal variable lacks coding — targetVariable = the variable, proposedValue = the coding (e.g. \"0 = No, 1 = Yes\").\n" +
        "- mark_resolved: the conflict is acceptable as-is and should stop being flagged.\n" +
        "- ignore: the conflict is noise for this study.\n" +
        "- no_safe_fix: you cannot safely fix it — the student must review manually. Never invent a fix.\n\n" +
        "Also classify EVERY conflict into one \"category\":\n" +
        "- \"safe_fix\": a routine correction you are confident about (add/map/rename/add_alias/update_coding/mark_resolved).\n" +
        "- \"safe_ignore\": routine noise that does not affect analysis — Google Form metadata (Timestamp, Username, Email Address, Score) and optional/administrative columns. Use action \"ignore\".\n" +
        "- \"manual_review\": important issues that could affect statistics and need the student's judgement — a MISSING OUTCOME variable, a MISSING REQUIRED variable, unclear important coding, a binary variable with more than two categories, a continuous variable with non-numeric values, or an INCOMPLETE SAMPLE SIZE. Use action \"no_safe_fix\" for these and explain what the student must decide.\n" +
        "- \"no_safe_fix\": you are unsure or cannot confidently solve it.\n\n" +
        "NEVER classify a missing outcome, a missing required variable, or an incomplete sample size as safe_ignore or mark_resolved — these are always manual_review. Never delete important variables, never invent sample data or statistics.\n\n" +
        "Return ONLY this JSON (no markdown, no commentary):\n" +
        "{\n" +
        "  \"fixes\": [\n" +
        "    {\"conflictKey\":\"string\",\"conflictTitle\":\"string\",\"category\":\"safe_fix|safe_ignore|manual_review|no_safe_fix\",\"action\":\"string\",\"explanation\":\"string\",\"confidence\":\"high|medium|low\",\"targetVariable\":\"string\",\"targetColumn\":\"string\",\"proposedValue\":\"string\"}\n" +
        "  ],\n" +
        "  \"warnings\": [\"string\"]\n" +
        "}";

    // Compact conflict-fix user prompt: short context + compact variables + CSV
    // headers + the active conflicts only. Never raw CSV rows, never proposal
    // text, never resolved/ignored conflicts.
    private static string BuildConflictFixesUserPrompt(ResearchProject p, List<ConflictFixInput> conflicts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Propose one fix per conflict below.");
        sb.AppendLine();
        sb.AppendLine("Project context (brief):");
        if (!string.IsNullOrWhiteSpace(p.Title)) sb.AppendLine("- Title: " + p.Title.Trim());
        if (!string.IsNullOrWhiteSpace(p.Specialty)) sb.AppendLine("- Specialty: " + p.Specialty.Trim());
        if (!string.IsNullOrWhiteSpace(p.StudyType)) sb.AppendLine("- Study type: " + p.StudyType.Trim());

        sb.AppendLine();
        sb.AppendLine("Extraction sheet variables (JSON):");
        sb.AppendLine(JsonSerializer.Serialize((p.Variables ?? new List<ResearchVariable>()).Select(v => new
        {
            v.VariableName, v.QuestionLabel, type = v.VariableType, role = v.Role, v.Coding, aliases = v.SourceColumnAliases
        })));

        if (p.CsvSampleSummary is { Columns.Count: > 0 } csv)
        {
            sb.AppendLine();
            sb.AppendLine("Uploaded CSV column headers only (no data rows): "
                + string.Join(", ", csv.Columns.Select(c => c.Name)));
        }

        sb.AppendLine();
        sb.AppendLine($"Active conflicts ({conflicts.Count}):");
        foreach (var c in conflicts)
            sb.AppendLine($"- conflictKey={c.ConflictKey} | kind={c.Kind} | severity={c.Severity} | title={c.Title}"
                + (string.IsNullOrWhiteSpace(c.VariableName) ? "" : $" | variable={c.VariableName}")
                + (string.IsNullOrWhiteSpace(c.ColumnName) ? "" : $" | column={c.ColumnName}"));
        return sb.ToString();
    }

    // COMPACT extraction-sheet prompt. The previous version appended the FULL
    // imported proposal (references and all) plus CSV sample values plus the
    // whole plan — a very heavy request that made the provider generate for
    // minutes and sometimes fail with a 500 before finishing. This version sends
    // only what variable generation actually needs: short project context, a
    // summarized accepted plan, the proposal's VARIABLE-RELATED sections only
    // (compact, capped), clean questionnaire questions, existing variable names,
    // and CSV column headers (never sample values or rows).
    private static string BuildExtractionSheetUserPrompt(ResearchProject p, string extraQuestionsContext)
    {
        static string Cap(string s, int max)
        {
            s = (s ?? "").Trim();
            return s.Length <= max ? s : s.Substring(0, max).TrimEnd() + "…";
        }

        var sb = new StringBuilder();
        sb.AppendLine("Build a data extraction sheet (variable list / data dictionary) for this student research project.");
        sb.AppendLine("Return VARIABLES ONLY — no sample rows, no participant data, no statistics, no prose.");
        sb.AppendLine("Use only the information below. Where something is not provided, add it to missingExpectedVariables instead of inventing it.");

        sb.AppendLine();
        sb.AppendLine("Project context (brief):");
        if (!string.IsNullOrWhiteSpace(p.Title)) sb.AppendLine("- Title: " + Cap(p.Title, 300));
        if (!string.IsNullOrWhiteSpace(p.Specialty)) sb.AppendLine("- Specialty: " + Cap(p.Specialty, 120));
        if (!string.IsNullOrWhiteSpace(p.StudyType)) sb.AppendLine("- Study type: " + Cap(p.StudyType, 120));
        if (!string.IsNullOrWhiteSpace(p.Aim)) sb.AppendLine("- Aim: " + Cap(p.Aim, 400));
        if (!string.IsNullOrWhiteSpace(p.Population)) sb.AppendLine("- Population: " + Cap(p.Population, 300));
        if (!string.IsNullOrWhiteSpace(p.Setting)) sb.AppendLine("- Setting: " + Cap(p.Setting, 300));

        // Accepted research plan, summarized to short lines (never full prose).
        if (p.Plan is { } plan)
        {
            sb.AppendLine();
            sb.AppendLine("Accepted research plan (summary):");
            void Line(string label, string val) { if (!string.IsNullOrWhiteSpace(val)) sb.AppendLine("- " + label + ": " + Cap(val, 500)); }
            Line("Research question", plan.ResearchQuestion);
            Line("Study design", plan.StudyDesign);
            Line("Primary objective", plan.PrimaryObjective);
            Line("Secondary objectives", plan.SecondaryObjectives);
            Line("Main variables", plan.MainVariables);
            Line("Inclusion criteria", plan.InclusionCriteria);
            Line("Exclusion criteria", plan.ExclusionCriteria);
        }
        else if (p.Recommendations is { HasStructuredContent: true } rec)
        {
            sb.AppendLine();
            sb.AppendLine("Research plan variables (from accepted recommendations):");
            foreach (var v in rec.SuggestedVariables.Take(40))
                sb.AppendLine("    • " + Cap(v.HeaderDisplay, 160) + " (" + Or2(v.Role, "role?") + ", " + Or2(v.VariableType, "type?") + ")");
        }

        // Proposal: VARIABLE-RELATED sections only, compact and capped — never the
        // full proposal and never references.
        var d = p.ProposalDraft;
        bool draftHasVarSections = d is not null &&
            (!string.IsNullOrWhiteSpace(d.Variables) || !string.IsNullOrWhiteSpace(d.DataCollection) || !string.IsNullOrWhiteSpace(d.StatisticalAnalysisPlan));
        if (draftHasVarSections)
        {
            sb.AppendLine();
            sb.AppendLine("Variable-related proposal sections (compact):");
            if (!string.IsNullOrWhiteSpace(d!.Variables)) sb.AppendLine("- Variables: " + Cap(d.Variables, 900));
            if (!string.IsNullOrWhiteSpace(d.DataCollection)) sb.AppendLine("- Data collection: " + Cap(d.DataCollection, 900));
            if (!string.IsNullOrWhiteSpace(d.StatisticalAnalysisPlan)) sb.AppendLine("- Planned analysis (for variable needs only): " + Cap(d.StatisticalAnalysisPlan, 900));
        }
        else if (!string.IsNullOrWhiteSpace(p.ImportedProposalText))
        {
            sb.AppendLine();
            sb.AppendLine("Imported proposal (compact extract, references removed):");
            sb.AppendLine(CompactProposalForPrompt(p.ImportedProposalText, 4000));
        }

        // Clean questionnaire / Google Form questions (already extracted locally).
        if (!string.IsNullOrWhiteSpace(extraQuestionsContext))
        {
            sb.AppendLine();
            sb.AppendLine("Questionnaire / form questions to incorporate:");
            sb.AppendLine("===== BEGIN QUESTIONS =====");
            sb.AppendLine(Cap(extraQuestionsContext, 8000));
            sb.AppendLine("===== END QUESTIONS =====");
        }

        // Existing variables: NAMES only (do not duplicate them).
        if (p.Variables is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Existing variables already in the sheet (do not duplicate): "
                + string.Join(", ", p.Variables.Where(v => !string.IsNullOrWhiteSpace(v.VariableName)).Select(v => v.VariableName.Trim()).Take(80)));
        }

        // CSV column HEADERS + inferred types only — never sample values or rows.
        if (p.CsvSampleSummary is { Columns.Count: > 0 } csv)
        {
            sb.AppendLine();
            sb.AppendLine("Uploaded CSV column headers only (no data rows): "
                + string.Join("; ", csv.Columns.Select(c => $"{c.Name} ({c.InferredType})")));
        }

        return sb.ToString();
    }

    private static string BuildQuestionsUserPrompt(ResearchProject p, string questionsText)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Convert the following questionnaire / form questions into data-dictionary variables.");
        if (!string.IsNullOrWhiteSpace(p.Title) || !string.IsNullOrWhiteSpace(p.Specialty))
        {
            sb.AppendLine();
            sb.AppendLine("For context only:");
            if (!string.IsNullOrWhiteSpace(p.Title)) sb.AppendLine("- Project title: " + p.Title.Trim());
            if (!string.IsNullOrWhiteSpace(p.Specialty)) sb.AppendLine("- Specialty: " + p.Specialty.Trim());
        }
        sb.AppendLine();
        sb.AppendLine("===== BEGIN QUESTIONS =====");
        sb.AppendLine((questionsText ?? "").Trim());
        sb.AppendLine("===== END QUESTIONS =====");
        return sb.ToString();
    }

    // COMPACT payload for the optional "Fix with Research AI" action. Sends ONLY
    // what is needed to correct conflicts — never the full proposal, never CSV
    // sample values/rows, never old generated prose. Keeps the request small so
    // it does not time out: short project context + the sheet variables (with
    // aliases/mappings) + CSV column HEADERS only + the active conflicts (capped
    // at 30, summarized beyond that).
    private static string BuildFixUserPrompt(ResearchProject p, ExtractionValidationReport report)
    {
        const int MaxConflicts = 30;
        var sb = new StringBuilder();
        sb.AppendLine("Correct the data extraction sheet using ONLY the active conflicts listed below.");
        sb.AppendLine("Return the FULL corrected variable list as JSON. Do not invent data, results, statistics, or references.");

        // Short project context only (no proposal, no plan prose).
        sb.AppendLine();
        sb.AppendLine("Project context (brief):");
        if (!string.IsNullOrWhiteSpace(p.Title)) sb.AppendLine("- Title: " + p.Title.Trim());
        if (!string.IsNullOrWhiteSpace(p.Specialty)) sb.AppendLine("- Specialty: " + p.Specialty.Trim());
        if (!string.IsNullOrWhiteSpace(p.StudyType)) sb.AppendLine("- Study type: " + p.StudyType.Trim());

        // Sheet variables, compact — includes source-column aliases so the model
        // understands existing CSV/form mappings.
        sb.AppendLine();
        sb.AppendLine("Extraction sheet variables (JSON):");
        sb.AppendLine(JsonSerializer.Serialize((p.Variables ?? new List<ResearchVariable>()).Select(v => new
        {
            v.VariableName, v.QuestionLabel, type = v.VariableType, level = v.MeasurementLevel,
            v.Role, v.Coding, aliases = v.SourceColumnAliases
        })));

        // CSV COLUMN HEADERS ONLY — never sample values or rows.
        if (p.CsvSampleSummary is { Columns.Count: > 0 } csv)
        {
            sb.AppendLine();
            sb.AppendLine("Uploaded CSV column headers only (no data rows): "
                + string.Join(", ", csv.Columns.Select(c => c.Name)));
        }

        // Active, unresolved conflicts (errors + warnings; already filtered of
        // ignored/resolved items). Capped and summarized past the cap.
        var items = new List<string>();
        if (report is not null)
        {
            foreach (var er in report.Errors) items.Add("ERROR: " + er);
            foreach (var w in report.Warnings) items.Add("WARNING: " + w);
        }
        sb.AppendLine();
        if (items.Count == 0)
        {
            sb.AppendLine("Active conflicts: none. Return the variables unchanged.");
        }
        else
        {
            sb.AppendLine($"Active conflicts to resolve ({items.Count}{(items.Count > MaxConflicts ? $", showing first {MaxConflicts}" : "")}):");
            foreach (var it in items.Take(MaxConflicts)) sb.AppendLine("- " + it);
            if (items.Count > MaxConflicts)
                sb.AppendLine($"- …and {items.Count - MaxConflicts} more similar items — apply the same kinds of corrections to them.");
        }
        return sb.ToString();
    }

    private static string Or2(string v, string fallback) => string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();

    private static string BuildRecommendationsUserPrompt(ResearchProject p)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Produce methodological recommendations for this student research project.");
        sb.AppendLine("Base everything only on the details below; where a detail is missing, say what is needed rather than inventing it.");
        sb.AppendLine();
        AppendProjectDetails(sb, p);

        // If the student imported an existing proposal, base the recommendations
        // primarily on it — derive the research question, objectives, variables,
        // analyses, methods, criteria, and ethics from the proposal itself, not
        // just the raw project fields. Still extraction-based: nothing invented.
        // The proposal is compacted first (references/DOIs/bibliography removed,
        // length-capped) so regeneration stays fast and does not time out — those
        // sections are never needed to produce methodological recommendations.
        if (!string.IsNullOrWhiteSpace(p.ImportedProposalText))
        {
            sb.AppendLine();
            sb.AppendLine("The student ALREADY has an existing proposal (below). Base your recommendations "
                + "primarily on it: derive the research question, objectives, variables, analyses, methods, "
                + "inclusion/exclusion criteria, and ethics from THIS proposal and stay consistent with it. "
                + "Do not invent anything that is not supported by the proposal or the project details.");
            sb.AppendLine("===== BEGIN EXISTING PROPOSAL =====");
            sb.AppendLine(CompactProposalForPrompt(p.ImportedProposalText, 12000));
            sb.AppendLine("===== END EXISTING PROPOSAL =====");
        }

        return sb.ToString();
    }

    // Lightweight, deterministic compaction used before sending an imported
    // proposal to the model: drops the References/Bibliography section and any
    // DOI/URL-heavy citation lines, collapses blank runs, and caps the length.
    // Reduces token load (faster, avoids timeouts) without losing the study's
    // methods/objectives — references are never needed for recommendations.
    private static string CompactProposalForPrompt(string raw, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var kept = new List<string>(lines.Length);
        var headingRe = new System.Text.RegularExpressions.Regex(
            @"^\s*(?:\d+[\.\)]\s*)?(references|reference list|bibliography|works cited|literature cited|citations)\s*:?\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (var line in lines)
        {
            if (headingRe.IsMatch(line)) break;   // cut everything from References on
            string t = line.Trim();
            if (t.IndexOf("doi.org", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            if (System.Text.RegularExpressions.Regex.IsMatch(t, @"\bdoi\s*:", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
            var urls = System.Text.RegularExpressions.Regex.Matches(t, @"https?://\S+|www\.\S+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (urls.Count > 0)
            {
                int urlChars = 0;
                foreach (System.Text.RegularExpressions.Match m in urls) urlChars += m.Value.Length;
                int nonSpace = t.Count(c => !char.IsWhiteSpace(c));
                if (nonSpace > 0 && (double)urlChars / nonSpace >= 0.5) continue;
            }
            kept.Add(line);
        }

        string result = System.Text.RegularExpressions.Regex.Replace(string.Join("\n", kept), @"(\r?\n){3,}", "\n\n").Trim();
        if (result.Length > maxChars) result = result.Substring(0, maxChars).TrimEnd();
        return result;
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

    private static string BuildExtractionUserPrompt(string proposalText, ResearchProject p)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Extract structured details from the EXISTING research proposal below.");
        sb.AppendLine("Only use what is actually written in it. Anything not present must be left empty and listed under missingOrWeakSections.");

        // A little existing-project context helps disambiguate (e.g. specialty),
        // but the model is told above to extract only from the proposal text.
        if (!string.IsNullOrWhiteSpace(p.Title) || !string.IsNullOrWhiteSpace(p.Specialty))
        {
            sb.AppendLine();
            sb.AppendLine("For context only (do NOT copy from here unless the proposal text also says it):");
            if (!string.IsNullOrWhiteSpace(p.Title)) sb.AppendLine("- Current project title: " + p.Title.Trim());
            if (!string.IsNullOrWhiteSpace(p.Specialty)) sb.AppendLine("- Current specialty: " + p.Specialty.Trim());
        }

        sb.AppendLine();
        sb.AppendLine("===== BEGIN PROPOSAL TEXT =====");
        sb.AppendLine(proposalText.Trim());
        sb.AppendLine("===== END PROPOSAL TEXT =====");
        return sb.ToString();
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
    bool TryParseExtraction(string text, out ProposalExtractionResult extraction);
    bool TryParseExtractionSheet(string text, out ExtractionSheetResult sheet);
    bool TryParseConflictFixes(string text, out ConflictFixResult fixes);
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

    public bool TryParseExtraction(string text, out ProposalExtractionResult extraction)
    {
        extraction = new ProposalExtractionResult { SourceMode = ResearchSourceMode.AiGenerated };

        foreach (string json in ExtractJsonCandidates(text))
        {
            try
            {
                var dto = JsonSerializer.Deserialize<ExtractionDto>(json, Opts);
                if (dto is null) continue;

                var r = new ProposalExtractionResult
                {
                    SourceMode = ResearchSourceMode.AiGenerated,
                    ExtractedTitle = Clean(dto.Title),
                    ExtractedSpecialty = Clean(dto.Specialty),
                    ExtractedStudyDesign = Clean(dto.StudyDesign),
                    ExtractedResearchQuestion = Clean(dto.ResearchQuestion),
                    ExtractedAim = Clean(dto.Aim),
                    ExtractedPrimaryObjective = Clean(dto.PrimaryObjective),
                    ExtractedSecondaryObjectives = CleanList(dto.SecondaryObjectives),
                    ExtractedPopulation = Clean(dto.Population),
                    ExtractedSetting = Clean(dto.Setting),
                    ExtractedTimePeriod = Clean(dto.TimePeriod),
                    ExtractedInclusionCriteria = CleanList(dto.InclusionCriteria),
                    ExtractedExclusionCriteria = CleanList(dto.ExclusionCriteria),
                    ExtractedDataCollection = CleanList(dto.DataCollection),
                    ExtractedEthics = CleanList(dto.Ethics),
                    ExtractedLimitations = CleanList(dto.Limitations),
                    ExtractedTimeline = Clean(dto.Timeline),
                    MissingOrWeakSections = CleanList(dto.MissingOrWeakSections),
                    Warnings = CleanList(dto.Warnings),
                    ConfidenceSummary = Clean(dto.ConfidenceSummary)
                };

                if (dto.Variables is not null)
                    foreach (var v in dto.Variables)
                    {
                        if (v is null) continue;
                        r.ExtractedVariables.Add(new ResearchVariableSuggestion
                        {
                            VariableName = Clean(v.Name),
                            VariableLabel = Clean(v.Label),
                            VariableType = Clean(v.Type),
                            Role = Clean(v.Role),
                            SuggestedCoding = Clean(v.Coding),
                            Notes = Clean(v.Notes)
                        });
                    }

                if (dto.SuggestedAnalyses is not null)
                    foreach (var a in dto.SuggestedAnalyses)
                    {
                        if (a is null) continue;
                        r.ExtractedSuggestedAnalyses.Add(new ResearchAnalysisSuggestion
                        {
                            AnalysisName = Clean(a.Name),
                            WhenToUse = Clean(a.WhenToUse),
                            VariablesNeeded = Clean(a.VariablesNeeded),
                            OutputExpected = Clean(a.OutputExpected),
                            Notes = Clean(a.Notes)
                        });
                    }

                if (dto.ProposalSections is not null)
                {
                    var s = dto.ProposalSections;
                    r.ExtractedProposalSections = new ExtractedProposalSections
                    {
                        Background = Clean(s.Background),
                        Rationale = Clean(s.Rationale),
                        Aim = Clean(s.Aim),
                        Objectives = Clean(s.Objectives),
                        Methods = Clean(s.Methods),
                        StudyDesign = Clean(s.StudyDesign),
                        Setting = Clean(s.Setting),
                        Population = Clean(s.Population),
                        InclusionCriteria = Clean(s.InclusionCriteria),
                        ExclusionCriteria = Clean(s.ExclusionCriteria),
                        Variables = Clean(s.Variables),
                        DataCollection = Clean(s.DataCollection),
                        StatisticalAnalysisPlan = Clean(s.StatisticalAnalysisPlan),
                        Ethics = Clean(s.Ethics),
                        Timeline = Clean(s.Timeline),
                        Limitations = Clean(s.Limitations)
                    };
                }

                if (r.HasAnyContent)
                {
                    extraction = r;
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

    public bool TryParseExtractionSheet(string text, out ExtractionSheetResult sheet)
    {
        sheet = new ExtractionSheetResult { SourceMode = ResearchSourceMode.AiGenerated };

        foreach (string json in ExtractJsonCandidates(text))
        {
            try
            {
                var dto = JsonSerializer.Deserialize<ExtractionSheetDto>(json, Opts);
                if (dto is null) continue;

                var r = new ExtractionSheetResult
                {
                    SourceMode = ResearchSourceMode.AiGenerated,
                    MissingExpectedVariables = CleanList(dto.MissingExpectedVariables),
                    ExtraOrUnexplainedColumns = CleanList(dto.ExtraOrUnexplainedColumns),
                    ChangeSummary = CleanList(dto.ChangeSummary),
                    Warnings = CleanList(dto.Warnings),
                    ConfidenceSummary = Clean(dto.ConfidenceSummary),
                    TargetSampleSizeText = Clean(dto.TargetSampleSize)
                };

                if (dto.Variables is not null)
                    foreach (var v in dto.Variables)
                    {
                        if (v is null) continue;
                        string name = Clean(v.VariableName);
                        string label = Clean(v.QuestionLabel);
                        if (name.Length == 0 && label.Length == 0) continue;   // skip empty rows
                        r.Variables.Add(new ResearchVariable
                        {
                            VariableName = name,
                            QuestionLabel = label,
                            VariableType = NormalizeOption(v.Type, ResearchVariableOptions.VariableTypes, "Unknown"),
                            MeasurementLevel = NormalizeOption(v.MeasurementLevel, ResearchVariableOptions.MeasurementLevels, "NotApplicable"),
                            Role = NormalizeOption(v.Role, ResearchVariableOptions.Roles, "Unknown"),
                            Coding = Clean(v.Coding),
                            ValueLabels = Clean(v.ValueLabels),
                            MissingValueRule = Clean(v.MissingRule),
                            Source = NormalizeOption(v.Source, ResearchVariableOptions.Sources, "AI Recommendation"),
                            IsRequired = v.Required ?? false,
                            Notes = Clean(v.Notes)
                        });
                    }

                if (r.HasAnyContent)
                {
                    sheet = r;
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

    // Parses the structured per-conflict fix proposals. Unknown actions are
    // normalized to "no_safe_fix" (needs manual review) — the parser never
    // invents a fix the model did not clearly propose. Any sample rows, counts,
    // or statistics the model might add are simply not mapped (discarded).
    public bool TryParseConflictFixes(string text, out ConflictFixResult fixes)
    {
        fixes = new ConflictFixResult();
        var allowedActions = new[]
        {
            "add_variable", "map_csv_column_to_variable", "rename_variable",
            "add_alias", "update_coding", "mark_resolved", "ignore", "no_safe_fix"
        };

        foreach (string json in ExtractJsonCandidates(text))
        {
            try
            {
                var dto = JsonSerializer.Deserialize<ConflictFixesDto>(json, Opts);
                if (dto?.Fixes is null) continue;

                var r = new ConflictFixResult { Warnings = CleanList(dto.Warnings) };
                foreach (var f in dto.Fixes)
                {
                    if (f is null) continue;
                    string key = Clean(f.ConflictKey);
                    if (key.Length == 0) continue;

                    string action = Clean(f.Action).ToLowerInvariant().Replace(' ', '_');
                    if (!allowedActions.Contains(action)) action = "no_safe_fix";

                    string confidence = Clean(f.Confidence).ToLowerInvariant();
                    if (confidence is not ("high" or "medium" or "low")) confidence = "low";

                    string category = Clean(f.Category).ToLowerInvariant().Replace(' ', '_');
                    if (category is not ("safe_fix" or "safe_ignore" or "manual_review" or "no_safe_fix")) category = "";

                    r.Fixes.Add(new ConflictFixProposal
                    {
                        ConflictKey = key,
                        ConflictTitle = Clean(f.ConflictTitle),
                        Action = action,
                        Category = category,
                        Explanation = Clean(f.Explanation),
                        Confidence = confidence,
                        TargetVariable = Clean(f.TargetVariable),
                        TargetColumn = Clean(f.TargetColumn),
                        ProposedValue = Clean(f.ProposedValue)
                    });
                }

                if (r.Fixes.Count > 0)
                {
                    fixes = r;
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

    // Snaps a free-text option to the nearest allowed value (case-insensitive,
    // then substring), falling back to a safe default. Keeps the sheet's combo
    // columns valid even if the model returns a slightly different spelling.
    private static string NormalizeOption(string? value, string[] allowed, string fallback)
    {
        string v = (value ?? "").Trim();
        if (v.Length == 0) return fallback;
        foreach (var a in allowed)
            if (string.Equals(a, v, StringComparison.OrdinalIgnoreCase)) return a;
        foreach (var a in allowed)
            if (a.Contains(v, StringComparison.OrdinalIgnoreCase) || v.Contains(a, StringComparison.OrdinalIgnoreCase)) return a;
        return fallback;
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

    private sealed class ExtractionDto
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("specialty")] public string? Specialty { get; set; }
        [JsonPropertyName("studyDesign")] public string? StudyDesign { get; set; }
        [JsonPropertyName("researchQuestion")] public string? ResearchQuestion { get; set; }
        [JsonPropertyName("aim")] public string? Aim { get; set; }
        [JsonPropertyName("primaryObjective")] public string? PrimaryObjective { get; set; }
        [JsonPropertyName("secondaryObjectives")] public List<string>? SecondaryObjectives { get; set; }
        [JsonPropertyName("population")] public string? Population { get; set; }
        [JsonPropertyName("setting")] public string? Setting { get; set; }
        [JsonPropertyName("timePeriod")] public string? TimePeriod { get; set; }
        [JsonPropertyName("inclusionCriteria")] public List<string>? InclusionCriteria { get; set; }
        [JsonPropertyName("exclusionCriteria")] public List<string>? ExclusionCriteria { get; set; }
        [JsonPropertyName("variables")] public List<VarDto?>? Variables { get; set; }
        [JsonPropertyName("suggestedAnalyses")] public List<AnaDto?>? SuggestedAnalyses { get; set; }
        [JsonPropertyName("dataCollection")] public List<string>? DataCollection { get; set; }
        [JsonPropertyName("ethics")] public List<string>? Ethics { get; set; }
        [JsonPropertyName("limitations")] public List<string>? Limitations { get; set; }
        [JsonPropertyName("timeline")] public string? Timeline { get; set; }
        [JsonPropertyName("proposalSections")] public ProposalDto? ProposalSections { get; set; }
        [JsonPropertyName("missingOrWeakSections")] public List<string>? MissingOrWeakSections { get; set; }
        [JsonPropertyName("warnings")] public List<string>? Warnings { get; set; }
        [JsonPropertyName("confidenceSummary")] public string? ConfidenceSummary { get; set; }
    }

    private sealed class ExtractionSheetDto
    {
        [JsonPropertyName("variables")] public List<SheetVarDto?>? Variables { get; set; }
        [JsonPropertyName("missingExpectedVariables")] public List<string>? MissingExpectedVariables { get; set; }
        [JsonPropertyName("extraOrUnexplainedColumns")] public List<string>? ExtraOrUnexplainedColumns { get; set; }
        [JsonPropertyName("changeSummary")] public List<string>? ChangeSummary { get; set; }
        [JsonPropertyName("warnings")] public List<string>? Warnings { get; set; }
        [JsonPropertyName("targetSampleSize")] public string? TargetSampleSize { get; set; }
        [JsonPropertyName("confidenceSummary")] public string? ConfidenceSummary { get; set; }
    }

    private sealed class SheetVarDto
    {
        [JsonPropertyName("variableName")] public string? VariableName { get; set; }
        [JsonPropertyName("questionLabel")] public string? QuestionLabel { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("measurementLevel")] public string? MeasurementLevel { get; set; }
        [JsonPropertyName("role")] public string? Role { get; set; }
        [JsonPropertyName("coding")] public string? Coding { get; set; }
        [JsonPropertyName("valueLabels")] public string? ValueLabels { get; set; }
        [JsonPropertyName("missingRule")] public string? MissingRule { get; set; }
        [JsonPropertyName("source")] public string? Source { get; set; }
        [JsonPropertyName("required")] public bool? Required { get; set; }
        [JsonPropertyName("notes")] public string? Notes { get; set; }
    }

    private sealed class ConflictFixesDto
    {
        [JsonPropertyName("fixes")] public List<ConflictFixDto?>? Fixes { get; set; }
        [JsonPropertyName("warnings")] public List<string>? Warnings { get; set; }
    }

    private sealed class ConflictFixDto
    {
        [JsonPropertyName("conflictKey")] public string? ConflictKey { get; set; }
        [JsonPropertyName("conflictTitle")] public string? ConflictTitle { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("action")] public string? Action { get; set; }
        [JsonPropertyName("explanation")] public string? Explanation { get; set; }
        [JsonPropertyName("confidence")] public string? Confidence { get; set; }
        [JsonPropertyName("targetVariable")] public string? TargetVariable { get; set; }
        [JsonPropertyName("targetColumn")] public string? TargetColumn { get; set; }
        [JsonPropertyName("proposedValue")] public string? ProposedValue { get; set; }
    }
}
