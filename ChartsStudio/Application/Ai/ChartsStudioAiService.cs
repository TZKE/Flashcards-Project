using AIFlashcardMaker.ChartsStudio.Domain.Ai;
using AIFlashcardMaker.ChartsStudio.Domain.Context;
using AIFlashcardMaker.ChartsStudio.Domain.Specs;
using AIFlashcardMaker.ChartsStudio.Domain.Themes;
using AIFlashcardMaker.ChartsStudio.Infrastructure.Ai;
using AIFlashcardMaker.CoreAi;

namespace AIFlashcardMaker.ChartsStudio.Application.Ai;

/// <summary>
/// Charts Studio Phase 6 — the AI Advisory Assistant workflow.
///
/// This is where the module's central AI commitment is enforced, in code:
///
///   • ADVISORY ONLY. AI reorders, explains, drafts and critiques. It never computes a number,
///     never touches the dataset, and nothing it returns is applied to a figure automatically —
///     a caption draft is a draft the user reviews.
///
///   • DETERMINISTIC-FIRST. Accessibility and consistency have a deterministic core that runs
///     with no model at all; AI, when present, only adds prose. The review tasks therefore work
///     fully offline, which is the architectural proof that AI is genuinely optional.
///
///   • DEGRADES, NEVER BLOCKS. If AI is unavailable or fails, the deterministic findings still
///     return, with a note saying what the model would have added. The caption task, which needs
///     the model to write prose, falls back to a plain deterministic caption skeleton built from
///     the same facts — so even "AI captioning" produces something useful offline.
///
/// It depends only on Core AI's runner (shared transport) and Charts Studio's own domain — never
/// on Research Lab.
/// </summary>
public sealed class ChartsStudioAiService
{
    private readonly AiCompletionRunner _runner;

    // Advisory tasks are quick; a shorter floor than research generations keeps them snappy.
    private const int CaptionMaxTokens = 400;
    private const int ReviewMaxTokens = 900;
    private const int AdvisoryTimeoutFloor = 90;

    public ChartsStudioAiService(AiCompletionRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    /// <summary>True when the model can be reached. The UI shows AI-only affordances only then;
    /// the deterministic reviews are offered regardless.</summary>
    public bool IsAiAvailable => _runner.IsAvailable;

    // ---- Caption ---------------------------------------------------------------------

    /// <summary>
    /// Drafts a caption for one figure. With AI: manuscript-quality prose using only the supplied
    /// facts. Without AI: a plain deterministic skeleton from the same facts — never nothing.
    /// The result is a DRAFT; applying it is the user's explicit action.
    /// </summary>
    public async Task<AiAdvisoryResult> DraftCaptionAsync(AiFigureContext figure, CancellationToken ct)
    {
        if (!_runner.IsAvailable)
            return new AiAdvisoryResult
            {
                Task = AiAdvisoryTask.Caption,
                DraftText = DeterministicCaption(figure),
                UsedAi = false,
                DegradationNote = "Sign in to your OrbitLab account for an AI-written caption. This is a plain draft built from the figure's facts."
            };

        try
        {
            var result = await _runner.CompleteAsync(new AiChatRequest
            {
                Action = "FigureCaption",
                SystemPrompt = FigureAiPrompts.CaptionSystem,
                UserPrompt = FigureAiPrompts.CaptionUser(figure),
                MaxOutputTokens = CaptionMaxTokens,
                MinTimeoutSeconds = AdvisoryTimeoutFloor
            }, ct).ConfigureAwait(false);

            string caption = FigureAiResponseParser.ParseCaption(result.Content);
            if (string.IsNullOrWhiteSpace(caption)) caption = DeterministicCaption(figure);

            return new AiAdvisoryResult { Task = AiAdvisoryTask.Caption, DraftText = caption, UsedAi = true };
        }
        catch (AiException ex)
        {
            return new AiAdvisoryResult
            {
                Task = AiAdvisoryTask.Caption,
                DraftText = DeterministicCaption(figure),
                UsedAi = false,
                DegradationNote = $"AI caption unavailable ({ex.Message}) — showing a plain draft from the figure's facts."
            };
        }
    }

    // ---- Critique --------------------------------------------------------------------

    public async Task<AiAdvisoryResult> CritiqueAsync(
        AiFigureContext figure, FigureSpec spec, ResolvedFigureStyle style, CancellationToken ct)
    {
        // Even critique has a deterministic floor: the accessibility findings for this figure.
        var deterministic = FigureAccessibilityAnalyzer.Analyze(figure.Index, spec, style);

        if (!_runner.IsAvailable)
            return new AiAdvisoryResult
            {
                Task = AiAdvisoryTask.Critique,
                Items = deterministic,
                UsedAi = false,
                DegradationNote = "Sign in for an AI critique. Showing automated presentation checks only."
            };

        try
        {
            var result = await _runner.CompleteAsync(new AiChatRequest
            {
                Action = "FigureCritique",
                SystemPrompt = FigureAiPrompts.CritiqueSystem,
                UserPrompt = FigureAiPrompts.CritiqueUser(figure),
                MaxOutputTokens = ReviewMaxTokens,
                MinTimeoutSeconds = AdvisoryTimeoutFloor
            }, ct).ConfigureAwait(false);

            var aiItems = FigureAiResponseParser.ParseItems(result.Content, figure.Index);
            return new AiAdvisoryResult
            {
                Task = AiAdvisoryTask.Critique,
                Items = Merge(deterministic, aiItems),
                UsedAi = aiItems.Count > 0,
                DegradationNote = aiItems.Count == 0 ? "The AI critique could not be read; showing automated checks only." : ""
            };
        }
        catch (AiException ex)
        {
            return new AiAdvisoryResult
            {
                Task = AiAdvisoryTask.Critique,
                Items = deterministic,
                UsedAi = false,
                DegradationNote = $"AI critique unavailable ({ex.Message}). Showing automated checks only."
            };
        }
    }

    // ---- Accessibility (deterministic core + optional AI prose) ----------------------

    public async Task<AiAdvisoryResult> ReviewAccessibilityAsync(
        AiFigureContext figure, FigureSpec spec, ResolvedFigureStyle style, CancellationToken ct)
    {
        var deterministic = FigureAccessibilityAnalyzer.Analyze(figure.Index, spec, style);

        if (!_runner.IsAvailable)
            return new AiAdvisoryResult
            {
                Task = AiAdvisoryTask.Accessibility,
                Items = deterministic,
                UsedAi = false,
                DegradationNote = deterministic.Count == 0
                    ? "No accessibility issues found by the automated checks. Sign in for an AI review."
                    : "Sign in for an AI review. Showing automated accessibility checks."
            };

        try
        {
            var result = await _runner.CompleteAsync(new AiChatRequest
            {
                Action = "FigureAccessibility",
                SystemPrompt = FigureAiPrompts.AccessibilitySystem,
                UserPrompt = FigureAiPrompts.AccessibilityUser(figure, deterministic),
                MaxOutputTokens = ReviewMaxTokens,
                MinTimeoutSeconds = AdvisoryTimeoutFloor
            }, ct).ConfigureAwait(false);

            var aiItems = FigureAiResponseParser.ParseItems(result.Content, figure.Index);
            return new AiAdvisoryResult
            {
                Task = AiAdvisoryTask.Accessibility,
                Items = Merge(deterministic, aiItems),
                UsedAi = aiItems.Count > 0
            };
        }
        catch (AiException)
        {
            return new AiAdvisoryResult
            {
                Task = AiAdvisoryTask.Accessibility,
                Items = deterministic,
                UsedAi = false,
                DegradationNote = "AI review unavailable. Showing automated accessibility checks."
            };
        }
    }

    // ---- Consistency (deterministic core + optional AI prose) ------------------------

    public async Task<AiAdvisoryResult> ReviewConsistencyAsync(
        AiFigureSetContext set, IReadOnlyList<FigureWithStyle> figures, CancellationToken ct)
    {
        var deterministic = FigureConsistencyAnalyzer.Analyze(figures);

        if (!_runner.IsAvailable)
            return new AiAdvisoryResult
            {
                Task = AiAdvisoryTask.Consistency,
                Items = deterministic,
                UsedAi = false,
                DegradationNote = deterministic.Count == 0
                    ? "The figures are consistent by the automated checks. Sign in for an AI review."
                    : "Sign in for an AI review. Showing automated consistency checks."
            };

        try
        {
            var result = await _runner.CompleteAsync(new AiChatRequest
            {
                Action = "SetConsistency",
                SystemPrompt = FigureAiPrompts.ConsistencySystem,
                UserPrompt = FigureAiPrompts.ConsistencyUser(set, deterministic),
                MaxOutputTokens = ReviewMaxTokens,
                MinTimeoutSeconds = AdvisoryTimeoutFloor
            }, ct).ConfigureAwait(false);

            var aiItems = FigureAiResponseParser.ParseItems(result.Content, 0);
            return new AiAdvisoryResult
            {
                Task = AiAdvisoryTask.Consistency,
                Items = Merge(deterministic, aiItems),
                UsedAi = aiItems.Count > 0
            };
        }
        catch (AiException)
        {
            return new AiAdvisoryResult
            {
                Task = AiAdvisoryTask.Consistency,
                Items = deterministic,
                UsedAi = false,
                DegradationNote = "AI review unavailable. Showing automated consistency checks."
            };
        }
    }

    // ---------------------------------------------------------------------------------

    /// <summary>Deterministic findings first (they are facts), AI suggestions after.</summary>
    private static IReadOnlyList<AiAdvisoryItem> Merge(
        IReadOnlyList<AiAdvisoryItem> deterministic, IReadOnlyList<AiAdvisoryItem> ai)
    {
        var all = new List<AiAdvisoryItem>(deterministic);
        all.AddRange(ai);
        return all
            .OrderByDescending(i => i.Source == AiAdvisorySource.Deterministic)
            .ThenByDescending(i => i.Severity)
            .ToList();
    }

    /// <summary>
    /// The offline caption fallback: a plain, correct sentence assembled from the same facts the
    /// AI would have used. Never invents — every number here came from the context. This is what
    /// makes "AI captioning" degrade to something useful rather than nothing.
    /// </summary>
    public static string DeterministicCaption(AiFigureContext f)
    {
        string subject = string.IsNullOrWhiteSpace(f.VariableName) ? "the measured variable" : f.VariableName;
        string units = string.IsNullOrWhiteSpace(f.Units) ? "" : $" ({f.Units})";
        string n = string.IsNullOrWhiteSpace(f.ValidN) ? "" : $" (n = {f.ValidN})";

        string lead = f.ChartTypeName.ToLowerInvariant() switch
        {
            "box plot" => $"Distribution of {subject}{units}{n}.",
            "bar chart" => $"{subject} by category{n}.",
            "mean ± sd" => $"Mean {subject}{units} with standard deviation{n}.",
            _ => $"{subject}{units}{n}."
        };

        if (f.SummaryFacts.Count > 0)
            lead += " " + CapitaliseFirst(string.Join(", ", f.SummaryFacts)) + ".";

        return lead;
    }

    private static string CapitaliseFirst(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
