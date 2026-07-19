using System.Text.Json;
using AIFlashcardMaker.ChartsStudio.Domain.Ai;

namespace AIFlashcardMaker.ChartsStudio.Infrastructure.Ai;

/// <summary>
/// Charts Studio Phase 6 — parses AI responses into advisory items or caption text.
///
/// Deterministic and tolerant: models wrap JSON in code fences, add stray prose, or return
/// slightly-off shapes. The parser strips fences, finds the JSON array, and skips malformed
/// entries rather than throwing — a partial answer is better than none, and a totally
/// unparseable answer becomes zero items (the caller then reports the degradation). Every AI
/// item is tagged <see cref="AiAdvisorySource.Ai"/> so the UI can distinguish it from a
/// computed fact. Pinned by tests over synthetic model output.
/// </summary>
public static class FigureAiResponseParser
{
    /// <summary>A caption is free text; only fences/quotes/labels are cleaned off.</summary>
    public static string ParseCaption(string raw)
    {
        string s = StripFences(raw).Trim();

        // Some models echo a leading label despite instructions; drop a single "Figure N." prefix.
        s = System.Text.RegularExpressions.Regex.Replace(s, @"^\s*Figure\s+\d+[\.:]\s*", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Drop symmetric surrounding quotes if the whole thing is quoted.
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
            s = s[1..^1].Trim();

        return s;
    }

    /// <summary>Parses a JSON array of advisory objects. Returns [] on anything unparseable.</summary>
    public static IReadOnlyList<AiAdvisoryItem> ParseItems(string raw, int figureIndex = 0)
    {
        var items = new List<AiAdvisoryItem>();

        string json = ExtractJsonArray(StripFences(raw));
        if (json.Length == 0) return items;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return items;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;

                string title = GetString(el, "title");
                string detail = GetString(el, "detail");
                if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(detail)) continue;

                items.Add(new AiAdvisoryItem
                {
                    Severity = ParseSeverity(GetString(el, "severity")),
                    Source = AiAdvisorySource.Ai,
                    FigureIndex = figureIndex,
                    Title = string.IsNullOrWhiteSpace(title) ? "Suggestion" : title.Trim(),
                    Detail = detail.Trim()
                });
            }
        }
        catch (JsonException)
        {
            // Unparseable → no items; the caller degrades gracefully with a note.
        }

        return items;
    }

    // ---------------------------------------------------------------------------------

    private static string StripFences(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        string s = raw.Trim();
        if (s.StartsWith("```"))
        {
            int firstNewline = s.IndexOf('\n');
            if (firstNewline >= 0) s = s[(firstNewline + 1)..];
            int lastFence = s.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0) s = s[..lastFence];
        }
        return s.Trim();
    }

    private static string ExtractJsonArray(string s)
    {
        int start = s.IndexOf('[');
        int end = s.LastIndexOf(']');
        return start >= 0 && end > start ? s[start..(end + 1)] : "";
    }

    private static string GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static AiAdvisorySeverity ParseSeverity(string s) => s.Trim().ToLowerInvariant() switch
    {
        "warning" => AiAdvisorySeverity.Warning,
        "info" => AiAdvisorySeverity.Info,
        _ => AiAdvisorySeverity.Suggestion
    };
}
