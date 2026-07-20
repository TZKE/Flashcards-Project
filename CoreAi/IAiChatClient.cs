namespace AIFlashcardMaker.CoreAi;

/// <summary>Low-level transport outcome, before it is mapped to a friendly error.</summary>
public enum AiTransportOutcome { Ok, NoSession, Timeout, Network }

/// <summary>Raw transport reply: outcome, HTTP status, and the upstream body (unparsed).</summary>
public sealed record AiTransportResponse(AiTransportOutcome Outcome, int Status, string Body)
{
    public bool IsSuccess => Outcome == AiTransportOutcome.Ok && Status is >= 200 and < 300;
}

/// <summary>
/// Core AI — the transport seam.
///
/// This is the ONE thing the completion runner depends on, and the ONE thing tests replace
/// with a fake. The production implementation wraps the app's existing shared proxy client
/// (OrbitLabAiProxyClient), so Core AI adds a reusable orchestration layer WITHOUT owning or
/// moving the transport — the transport stays exactly where Research Lab and the flashcard AI
/// already share it.
/// </summary>
public interface IAiChatClient
{
    /// <summary>True when a backend is configured and the user has a live session.</summary>
    bool IsAvailable { get; }

    /// <summary>An identifier for the model in use, for the manifest/diagnostics. The desktop
    /// app never selects the model (the backend forces it), so this is descriptive only.</summary>
    string ModelLabel { get; }

    /// <summary>Builds the provider chat body from a system+user prompt and knobs.</summary>
    string BuildBody(string systemPrompt, string userPrompt, double temperature, int maxTokens);

    /// <summary>Extracts the assistant message content from an upstream body.</summary>
    string ExtractContent(string body);

    /// <summary>True when the model stopped because it hit its token ceiling.</summary>
    bool WasTruncated(string body);

    /// <summary>Sends a ready-made chat body. Never throws — failures come back as an outcome.</summary>
    Task<AiTransportResponse> SendAsync(string chatBodyJson, int timeoutSeconds, CancellationToken cancellationToken);
}
