namespace AIFlashcardMaker.CoreAi;

/// <summary>
/// Core AI — machine-readable failure category. Callers branch on this, never on message
/// text, so wording can change freely without breaking retry/degradation logic. Mirrors the
/// categories Research Lab's own (untouched) AI layer uses, so both speak the same language.
/// </summary>
public enum AiErrorCategory
{
    Auth,
    Timeout,
    RateLimit,
    Overload,
    PromptTooLong,
    BadRequest,
    EmptyResponse,
    Truncated,
    ParseFailure,
    Network,
    ProviderError,
    Unknown
}

/// <summary>
/// Core AI — one AI completion request, fully generic and provider-neutral.
///
/// This is deliberately just prompts and knobs. It has no field capable of carrying a data row
/// — the data-free guarantee that matters for Charts Studio is enforced ABOVE this layer, where
/// the prompt is built from a bounded context that never sees a dataset. Core AI simply
/// transports whatever text it is given.
/// </summary>
public sealed class AiChatRequest
{
    /// <summary>Short label for content-free diagnostics, e.g. "FigureCaption". Never logged text.</summary>
    public required string Action { get; init; }

    public required string SystemPrompt { get; init; }
    public required string UserPrompt { get; init; }

    /// <summary>Low by default: advisory tasks want consistency, not creativity.</summary>
    public double Temperature { get; init; } = 0.2;

    /// <summary>Output ceiling. Small for captions, larger for multi-point critiques.</summary>
    public int MaxOutputTokens { get; init; } = 2000;

    /// <summary>Per-call timeout floor in seconds; the policy may raise it, never lower it
    /// below a safe minimum.</summary>
    public int MinTimeoutSeconds { get; init; } = 120;
}

/// <summary>Content-free context about a completed call, for diagnostics on a late (parse) failure.</summary>
public readonly struct AiCallInfo
{
    public string Model { get; init; }
    public int InputChars { get; init; }
    public int MaxTokens { get; init; }
    public int TimeoutSeconds { get; init; }
    public double ElapsedSeconds { get; init; }
    public int? StatusCode { get; init; }
}

/// <summary>Successful completion: the model's text plus content-free call context.</summary>
public sealed class AiChatResult
{
    public required string Content { get; init; }
    public required AiCallInfo Info { get; init; }
}

/// <summary>
/// Core AI — the exception every AI failure surfaces as. Carries a machine-readable category
/// and a content-free diagnostic line (sizes, status, timing — never keys, prompts or
/// responses) that a UI can show behind an "Open details" action.
/// </summary>
public sealed class AiException : Exception
{
    public AiErrorCategory Category { get; }
    public bool IsTimeout => Category == AiErrorCategory.Timeout;
    public string? Diagnostics { get; }

    public AiException(string message, AiErrorCategory category, string? diagnostics = null)
        : base(message)
    {
        Category = category;
        Diagnostics = diagnostics;
    }
}
