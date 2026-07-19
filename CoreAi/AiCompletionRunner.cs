using System.Diagnostics;
using System.IO;

namespace AIFlashcardMaker.CoreAi;

/// <summary>Core AI — timeout policy. One place decides how long an AI call may run.</summary>
public static class AiTimeoutPolicy
{
    public const int DefaultFloorSeconds = 120;
    public const int MaxSeconds = 2700;   // 45 minutes, matching the app's long-running ceiling

    public static int Resolve(int requestedFloorSeconds) =>
        Math.Clamp(Math.Max(requestedFloorSeconds, DefaultFloorSeconds), 30, MaxSeconds);
}

/// <summary>
/// Core AI — content-free diagnostics. Records call SHAPE (action, model, sizes, timeout,
/// elapsed, status, category) and never prompts, responses, keys or user text. Written to a
/// dedicated Core AI log so it never mingles with Research Lab's own log.
/// </summary>
public static class AiDiagnostics
{
    public static string? LastEntry { get; private set; }

    public static string Log(string action, int timeoutSeconds, double elapsedSeconds,
        int? statusCode = null, bool bodyEmpty = false, string? category = null,
        string? model = null, int? inputChars = null, int? maxTokens = null, string? outcome = null)
    {
        string entry =
            $"[{DateTime.Now:u}] action={action} " +
            $"model={(string.IsNullOrWhiteSpace(model) ? "n/a" : model)} " +
            $"inputChars={(inputChars?.ToString() ?? "n/a")} maxTokens={(maxTokens?.ToString() ?? "n/a")} " +
            $"timeout={timeoutSeconds}s elapsed={elapsedSeconds:F1}s status={(statusCode?.ToString() ?? "n/a")} " +
            $"category={(category ?? "n/a")} emptyBody={bodyEmpty} outcome={(outcome ?? "n/a")}";

        LastEntry = entry;
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIFlashcardMaker");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "core_ai.log"), entry + Environment.NewLine);
        }
        catch { /* logging must never throw */ }
        return entry;
    }
}

/// <summary>
/// Core AI — the reusable completion orchestration.
///
/// This is the generic distillation of the (untouched, still-in-production) orchestration
/// Research Lab evolved: run a request through the transport, map every transport outcome and
/// HTTP status to a friendly message + machine-readable category, distinguish a truncated reply
/// (finish_reason "length") from a genuinely empty one, and log content-free diagnostics
/// throughout. Charts Studio's AI layer builds on THIS instead of hand-rolling a third copy.
///
/// It is pure orchestration over the IAiChatClient seam, so it — and everything above it — is
/// fully headless-testable with a fake client that returns canned transport responses.
/// </summary>
public sealed class AiCompletionRunner
{
    private readonly IAiChatClient _client;

    public AiCompletionRunner(IAiChatClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public bool IsAvailable => _client.IsAvailable;

    public string ModelLabel => _client.ModelLabel;

    /// <summary>
    /// Runs a completion. Returns the model's text on success; throws <see cref="AiException"/>
    /// with a category on any failure. The message text is deliberately generic ("your work is
    /// safe" style) — Core AI never mentions keys, tokens, endpoints or providers.
    /// </summary>
    public async Task<AiChatResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_client.IsAvailable)
            throw new AiException("Sign in to your OrbitLab account to use AI features.", AiErrorCategory.Auth,
                AiDiagnostics.Log(request.Action, 0, 0, category: "auth", outcome: "not available"));

        string body = _client.BuildBody(request.SystemPrompt, request.UserPrompt, request.Temperature, request.MaxOutputTokens);
        int timeoutSeconds = AiTimeoutPolicy.Resolve(request.MinTimeoutSeconds);
        int inputChars = request.SystemPrompt.Length + request.UserPrompt.Length;
        string model = _client.ModelLabel;

        string LogCall(int? status = null, bool empty = false, string? category = null, string? outcome = null, double elapsed = 0)
            => AiDiagnostics.Log(request.Action, timeoutSeconds, elapsed, status, empty, category, model, inputChars, request.MaxOutputTokens, outcome);

        var sw = Stopwatch.StartNew();
        var r = await _client.SendAsync(body, timeoutSeconds, cancellationToken).ConfigureAwait(false);
        sw.Stop();
        double elapsed = sw.Elapsed.TotalSeconds;

        switch (r.Outcome)
        {
            case AiTransportOutcome.NoSession:
                throw new AiException("Sign in to your OrbitLab account to use AI features.", AiErrorCategory.Auth,
                    LogCall(category: "auth", outcome: "no session", elapsed: elapsed));
            case AiTransportOutcome.Timeout:
                throw new AiException("The AI did not finish in time. Your work is safe — you can try again.", AiErrorCategory.Timeout,
                    LogCall(category: "timeout", outcome: "timeout", elapsed: elapsed));
            case AiTransportOutcome.Network:
                throw new AiException("Could not reach OrbitLab. Check your connection and try again.", AiErrorCategory.Network,
                    LogCall(category: "network", outcome: "network", elapsed: elapsed));
        }

        if (!r.IsSuccess)
        {
            (string msg, AiErrorCategory category) = r.Status switch
            {
                401 => ("Your session has expired. Please log out and back in, then retry.", AiErrorCategory.Auth),
                403 => ("Your OrbitLab subscription is not active for AI features.", AiErrorCategory.Auth),
                429 => ("You have reached today's AI limit. Please try again tomorrow.", AiErrorCategory.RateLimit),
                413 => ("The request was too large. Try a smaller selection.", AiErrorCategory.PromptTooLong),
                400 => ("OrbitLab could not process that AI request. Retrying will not help.", AiErrorCategory.BadRequest),
                504 or 408 => ("The AI stopped before finishing. Your work is safe — try again.", AiErrorCategory.Timeout),
                502 or 503 => ("AI is temporarily unavailable. Try again in a moment.", AiErrorCategory.Overload),
                >= 500 => ("AI had a server error. Your work is safe — please try again shortly.", AiErrorCategory.ProviderError),
                _ => ("AI returned an error. Your work is safe — please try again.", AiErrorCategory.ProviderError)
            };
            throw new AiException(msg, category, LogCall(status: r.Status, category: category.ToString(), elapsed: elapsed));
        }

        string content = _client.ExtractContent(r.Body);
        if (string.IsNullOrWhiteSpace(content))
        {
            if (_client.WasTruncated(r.Body))
                throw new AiException(
                    "The AI ran out of room before finishing. Try a shorter or simpler request.",
                    AiErrorCategory.Truncated,
                    LogCall(status: r.Status, empty: true, category: "truncated", elapsed: elapsed));

            throw new AiException("AI returned an empty response. Please try again.", AiErrorCategory.EmptyResponse,
                LogCall(status: r.Status, empty: true, category: "empty_response", elapsed: elapsed));
        }

        LogCall(status: r.Status, category: "success", outcome: "success", elapsed: elapsed);

        return new AiChatResult
        {
            Content = content,
            Info = new AiCallInfo
            {
                Model = model,
                InputChars = inputChars,
                MaxTokens = request.MaxOutputTokens,
                TimeoutSeconds = timeoutSeconds,
                ElapsedSeconds = elapsed,
                StatusCode = r.Status
            }
        };
    }
}
