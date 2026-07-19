namespace AIFlashcardMaker.CoreAi;

/// <summary>
/// Core AI — the production transport, a thin adapter over the app's existing shared proxy
/// client (OrbitLabAiProxyClient).
///
/// This is the ONLY class in Core AI that references the concrete transport. Core AI does not
/// move, own or reimplement the transport — the proxy stays where the whole app already shares
/// it (Phase 8's single production AI path). This adapter just re-expresses it behind the
/// IAiChatClient seam so the completion runner is testable with a fake client.
/// </summary>
public sealed class ProxyAiChatClient : IAiChatClient
{
    private readonly OrbitLabAiProxyClient _proxy;

    public ProxyAiChatClient(OrbitLabAiProxyClient proxy)
    {
        _proxy = proxy ?? throw new ArgumentNullException(nameof(proxy));
    }

    public bool IsAvailable => _proxy.IsAvailable;

    // The backend forces the real model; the client can neither choose nor learn it, so this is
    // a stable descriptive label for the manifest and diagnostics.
    public string ModelLabel => "server-managed";

    public string BuildBody(string systemPrompt, string userPrompt, double temperature, int maxTokens) =>
        OrbitLabAiProxyClient.BuildChatBody(systemPrompt, userPrompt, temperature, maxTokens);

    public string ExtractContent(string body) => OrbitLabAiProxyClient.ExtractContent(body);

    public bool WasTruncated(string body) => OrbitLabAiProxyClient.WasTruncated(body);

    public async Task<AiTransportResponse> SendAsync(string chatBodyJson, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var r = await _proxy.SendChatAsync(chatBodyJson, timeoutSeconds, cancellationToken).ConfigureAwait(false);

        var outcome = r.Outcome switch
        {
            AiProxyOutcome.Ok => AiTransportOutcome.Ok,
            AiProxyOutcome.NoSession => AiTransportOutcome.NoSession,
            AiProxyOutcome.Timeout => AiTransportOutcome.Timeout,
            AiProxyOutcome.Network => AiTransportOutcome.Network,
            _ => AiTransportOutcome.Network
        };

        return new AiTransportResponse(outcome, r.Status, r.Body);
    }
}
