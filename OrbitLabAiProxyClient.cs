using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AIFlashcardMaker;

/// <summary>
/// Phase 8 hardening: the SINGLE production transport for every AI feature in the
/// desktop app (flashcards, Research Lab, and any future AI helper).
///
/// It sends OpenAI-style chat-completion requests to the OrbitLab backend proxy at
/// <c>{BackendBaseUrl}/api/ai/chat/completions</c>, authenticated with the user's
/// current LICENSE SESSION TOKEN (never a provider API key). The desktop app never
/// holds, stores, or transmits the Z.ai key, never chooses the upstream URL beyond
/// the configured backend, and never selects the model (the backend forces it).
///
/// No prompt or response body is logged here. User-facing errors never mention
/// keys, tokens, base URLs, proxies, or environment variables.
/// </summary>
public enum AiProxyOutcome { Ok, NoSession, Timeout, Network }

public sealed record AiProxyResponse(AiProxyOutcome Outcome, int Status, string Body)
{
    public bool IsSuccess => Outcome == AiProxyOutcome.Ok && Status is >= 200 and < 300;
}

public sealed class OrbitLabAiProxyClient
{
    // The real per-request timeout is enforced per call via a linked CTS; disable
    // the handler-level timeout so long research generations are not cut off.
    private static readonly HttpClient Http = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    private readonly Func<string?> _sessionToken;

    public OrbitLabAiProxyClient(Func<string?> sessionTokenProvider) => _sessionToken = sessionTokenProvider;

    /// <summary>True when a backend is configured and the user has a live session.</summary>
    public bool IsAvailable => AppConfig.IsBackendConfigured && !string.IsNullOrEmpty(_sessionToken());

    /// <summary>
    /// POSTs a ready-made chat-completions JSON body to the proxy. Returns the raw
    /// upstream body + status, or a NoSession/Timeout/Network outcome. Never throws.
    /// </summary>
    public async Task<AiProxyResponse> SendChatAsync(string chatBodyJson, int timeoutSeconds, CancellationToken cancellationToken)
    {
        string? token = _sessionToken();
        if (!AppConfig.IsBackendConfigured || string.IsNullOrEmpty(token))
            return new AiProxyResponse(AiProxyOutcome.NoSession, 0, "");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)));
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, AppConfig.BackendBaseUrl + "/api/ai/chat/completions");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content = new StringContent(chatBodyJson, Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req, cts.Token);
            string body = await resp.Content.ReadAsStringAsync(cts.Token);
            return new AiProxyResponse(AiProxyOutcome.Ok, (int)resp.StatusCode, body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AiProxyResponse(AiProxyOutcome.Timeout, 0, "");
        }
        catch (HttpRequestException)
        {
            return new AiProxyResponse(AiProxyOutcome.Network, 0, "");
        }
    }

    /// <summary>Builds a minimal chat-completions body. The model is a placeholder — the backend overrides it.</summary>
    public static string BuildChatBody(string systemPrompt, string userPrompt, double temperature, int maxTokens)
    {
        var body = new
        {
            model = "orbitlab-managed",   // ignored by the backend, which forces ZAI_MODEL
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
            temperature,
            max_tokens = maxTokens,
            stream = false,
        };
        return JsonSerializer.Serialize(body);
    }

    /// <summary>Reads choices[0].message.content from a standard chat-completions body.</summary>
    public static string ExtractContent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var msg)
                && msg.TryGetProperty("content", out var content))
                return content.GetString()?.Trim() ?? "";
        }
        catch { /* malformed upstream body → empty */ }
        return "";
    }

    /// <summary>
    /// Reads choices[0].finish_reason from an upstream chat-completion body.
    /// "length" means the model was cut off before finishing — which arrives as
    /// HTTP 200 with empty or truncated content and is otherwise indistinguishable
    /// from a genuinely malformed reply. Returns "" when absent or unreadable.
    /// </summary>
    public static string ExtractFinishReason(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("finish_reason", out var reason)
                && reason.ValueKind == JsonValueKind.String)
                return reason.GetString() ?? "";
        }
        catch { /* malformed upstream body → unknown */ }
        return "";
    }

    /// <summary>True when the model stopped because it hit the token ceiling.</summary>
    public static bool WasTruncated(string json) =>
        string.Equals(ExtractFinishReason(json), "length", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Dev-only escape hatch. Defaults to OFF; only a trusted environment variable can
/// enable local engineering tools. A saved user preference or a leftover local key
/// can NEVER turn this on, and release/production users never see any AI controls.
/// </summary>
public static class AiDevTools
{
    public static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("ORBITLAB_ENABLE_AI_DEV_TOOLS"), "true",
            StringComparison.OrdinalIgnoreCase);
}
