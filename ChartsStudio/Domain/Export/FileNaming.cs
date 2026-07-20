using System.Text;

namespace AIFlashcardMaker.ChartsStudio.Domain.Export;

/// <summary>Naming schemes offered in the export dialog.</summary>
public enum ExportNameTemplate
{
    /// <summary>Figure_1, Figure_2 …</summary>
    FigureNumber,

    /// <summary>The figure's (patched) title.</summary>
    Title,

    /// <summary>Study_Figure_1 …</summary>
    StudyFigureNumber,

    /// <summary>Study_BoxPlot_Title …</summary>
    StudyTypeTitle
}

/// <summary>
/// Charts Studio Phase 5 — makes arbitrary research text safe as a Windows file name.
///
/// Titles come from variable labels, which come from questionnaire headers — so they contain
/// question marks, colons, slashes, quotes and worse. Pure and exhaustively unit-tested,
/// because a bad file name discovered at submission time is exactly the failure this phase
/// exists to prevent.
/// </summary>
public static class FileNameSanitizer
{
    private static readonly char[] Illegal = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
        "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"
    };

    public const int MaxLength = 120;

    public static string Sanitize(string? raw, string fallback = "Figure")
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;

        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            if (char.IsControl(c)) continue;
            sb.Append(Illegal.Contains(c) ? '_' : c);
        }

        // Whitespace runs become single underscores: "BMI  by  group" → "BMI_by_group".
        string result = string.Join("_",
            sb.ToString().Split(' ', '\t').Where(p => p.Length > 0));

        // Trailing dots and spaces are legal to create and broken to use on Windows.
        result = result.Trim('.', ' ', '_');

        if (result.Length == 0) return fallback;
        if (result.Length > MaxLength) result = result[..MaxLength].TrimEnd('.', ' ', '_');

        // CON.png is still CON to the OS.
        if (Reserved.Contains(result)) result = "_" + result;

        return result;
    }
}

/// <summary>Everything a template can draw on for one figure.</summary>
public sealed class ExportNameContext
{
    public required int Index { get; init; }          // 1-based, shelf order
    public required string Title { get; init; }        // resolved (patched) title
    public required string StudyName { get; init; }
    public required string ChartTypeName { get; init; }
}

/// <summary>
/// Charts Studio Phase 5 — turns a template plus figure context into unique file names.
///
/// Uniqueness is guaranteed WITHIN a batch (case-insensitively, because NTFS is): two figures
/// titled "Distribution of BMI" become "…", "…_2". Deterministic — the same shelf exports to
/// the same names every time, which is what makes a re-export reproducible and diffable.
/// </summary>
public sealed class ExportFileNamer
{
    private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

    public string NameFor(ExportNameTemplate template, ExportNameContext ctx)
    {
        string baseName = template switch
        {
            ExportNameTemplate.FigureNumber => $"Figure_{ctx.Index}",
            ExportNameTemplate.Title => FileNameSanitizer.Sanitize(ctx.Title, $"Figure_{ctx.Index}"),
            ExportNameTemplate.StudyFigureNumber =>
                $"{FileNameSanitizer.Sanitize(ctx.StudyName, "Study")}_Figure_{ctx.Index}",
            ExportNameTemplate.StudyTypeTitle =>
                $"{FileNameSanitizer.Sanitize(ctx.StudyName, "Study")}_" +
                $"{FileNameSanitizer.Sanitize(ctx.ChartTypeName, "Chart")}_" +
                $"{FileNameSanitizer.Sanitize(ctx.Title, ctx.Index.ToString())}",
            _ => $"Figure_{ctx.Index}"
        };

        string candidate = baseName;
        int suffix = 2;
        while (!_used.Add(candidate))
            candidate = $"{baseName}_{suffix++}";

        return candidate;
    }
}
