using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace AIFlashcardMaker;

// ---------------------------------------------------------------------------
// Research Lab — deterministic DOCX exporter for the Report Builder.
//
// Writes a minimal, valid Open XML (.docx) package from the report's plain text
// using ONLY the built-in System.IO.Compression (no external NuGet package).
// The .docx is an OPC zip with three parts: [Content_Types].xml, _rels/.rels and
// word/document.xml. Body text is rendered in a monospace font so the report's
// aligned tables/aggregate blocks stay readable; heading lines (those underlined
// by === or --- in the text report) become bold headings.
//
// No AI, no network, no new statistics. It only re-lays-out already-composed text
// that the report builder produced (aggregate-only). Deterministic: fixed part
// bytes and fixed zip entry timestamps mean identical input → identical file.
// ---------------------------------------------------------------------------
public static class ResearchLabDocxExporter
{
    // Fixed timestamp so the produced .docx is byte-deterministic for a given input.
    private static readonly DateTimeOffset FixedStamp = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static void Export(string reportText, string path)
    {
        string documentXml = BuildDocumentXml(reportText ?? "");
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        WriteEntry(zip, "[Content_Types].xml", ContentTypesXml);
        WriteEntry(zip, "_rels/.rels", RelsXml);
        WriteEntry(zip, "word/document.xml", documentXml);
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = FixedStamp;
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    private const string ContentTypesXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
        "</Types>";

    private const string RelsXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
        "</Relationships>";

    private static string BuildDocumentXml(string text)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>");

        string[] lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string next = i + 1 < lines.Length ? lines[i + 1] : "";

            if (line.Length > 0 && IsUnderline(next, '='))
            {
                sb.Append(Heading(line, halfPoints: 30));   // H1 (15pt bold)
                i++;                                          // consume the underline
            }
            else if (line.Length > 0 && IsUnderline(next, '-'))
            {
                sb.Append(Heading(line, halfPoints: 24));   // H2 (12pt bold)
                i++;
            }
            else if (line.Length == 0)
            {
                sb.Append("<w:p/>");
            }
            else
            {
                sb.Append(Mono(line));
            }
        }

        // Letter page with 1-inch margins.
        sb.Append("<w:sectPr><w:pgSz w:w=\"12240\" w:h=\"15840\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\"/></w:sectPr>");
        sb.Append("</w:body></w:document>");
        return sb.ToString();
    }

    // A "heading underline" is a run of exactly one repeated character (=== or ---)
    // with no other characters — this never matches table borders (which contain +).
    private static bool IsUnderline(string s, char c)
    {
        if (s.Length < 3) return false;
        foreach (char ch in s) if (ch != c) return false;
        return true;
    }

    private static string Mono(string line) =>
        "<w:p><w:pPr><w:rPr><w:rFonts w:ascii=\"Courier New\" w:hAnsi=\"Courier New\"/><w:sz w:val=\"18\"/></w:rPr></w:pPr>" +
        "<w:r><w:rPr><w:rFonts w:ascii=\"Courier New\" w:hAnsi=\"Courier New\"/><w:sz w:val=\"18\"/></w:rPr>" +
        "<w:t xml:space=\"preserve\">" + Esc(line) + "</w:t></w:r></w:p>";

    private static string Heading(string line, int halfPoints) =>
        "<w:p><w:pPr><w:spacing w:before=\"120\" w:after=\"40\"/><w:rPr><w:b/><w:sz w:val=\"" + halfPoints + "\"/></w:rPr></w:pPr>" +
        "<w:r><w:rPr><w:b/><w:sz w:val=\"" + halfPoints + "\"/></w:rPr>" +
        "<w:t xml:space=\"preserve\">" + Esc(line) + "</w:t></w:r></w:p>";

    private static string Esc(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                // Strip control chars that are invalid in XML 1.0 (keep tab).
                default:
                    if (c < 0x20 && c != '\t') sb.Append(' ');
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
