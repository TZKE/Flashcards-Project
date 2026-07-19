using System.Text;

namespace AIFlashcardMaker.ChartsStudio.Domain.Export;

/// <summary>One figure's caption inputs, already resolved (patched titles, patched captions).</summary>
public sealed class CaptionEntry
{
    public required int Index { get; init; }          // 1-based, shelf order
    public required string Title { get; init; }
    public required string Caption { get; init; }      // may be empty — title still lists
    public required string FileName { get; init; }     // figure file this caption belongs to
}

/// <summary>
/// Charts Studio Phase 5 — composes the captions documents that travel with a figure export.
///
/// Pure text in, text out. Numbering follows SHELF ORDER, the order the user arranged, so
/// "Figure 3" in the captions file is the same figure the manifest and the file names call 3.
/// DOCX output is a future extension: it belongs to the same Report Builder machinery Research
/// Lab already has, and gets added there rather than duplicated here.
/// </summary>
public static class CaptionComposer
{
    public static string ComposeText(IReadOnlyList<CaptionEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Figure captions");
        sb.AppendLine(new string('=', 15));
        sb.AppendLine();

        foreach (var e in entries)
        {
            sb.Append("Figure ").Append(e.Index).Append(". ").AppendLine(e.Title.TrimEnd('.') + ".");
            if (!string.IsNullOrWhiteSpace(e.Caption))
                sb.AppendLine(e.Caption.Trim());
            sb.Append("File: ").AppendLine(e.FileName);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string ComposeMarkdown(IReadOnlyList<CaptionEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Figure captions");
        sb.AppendLine();

        foreach (var e in entries)
        {
            sb.Append("**Figure ").Append(e.Index).Append(".** ").AppendLine(e.Title.TrimEnd('.') + ".");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(e.Caption))
            {
                sb.AppendLine(e.Caption.Trim());
                sb.AppendLine();
            }
            sb.Append("*File:* `").Append(e.FileName).AppendLine("`");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
