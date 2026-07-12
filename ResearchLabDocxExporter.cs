using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace AIFlashcardMaker;

// ---------------------------------------------------------------------------
// Research Lab — deterministic DOCX exporter for the Report Builder.
//
// Writes a valid Open XML (.docx) package with ONLY the built-in
// System.IO.Compression (no external NuGet package). The rich Export(result, …)
// overload renders the report's structured blocks into real Word styles
// (Title / Heading 1–2 / Normal) and real Word tables (overview/study-design/
// dataset/variables/results as key-value or grid tables), with the aggregate
// technical blocks in a monospace style. Wide tables use fixed column widths and
// a small font so cells wrap instead of overflowing.
//
// Researcher-facing default: system dataset columns (Sample_ID, Sample_Type,
// Timestamp, Username) are omitted from the columns table and a note lists them.
// (TXT/Markdown are unchanged.) No AI, no network, no new statistics — it only
// re-lays-out already-composed aggregate content. Deterministic: fixed part bytes
// and fixed zip entry timestamps mean identical input → identical file.
// ---------------------------------------------------------------------------
public static class ResearchLabDocxExporter
{
    private static readonly DateTimeOffset FixedStamp = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const int PageWidthTwips = 9360;   // Letter, 1-inch margins (6.5in * 1440)

    // Rich export from the structured report (preferred; used by the UI).
    public static void Export(ResearchLabReportBuilderResult result, string path)
        => WritePackage(path, BuildDocumentXmlFromBlocks(result));

    // Simple export from plain report text (back-compatible; monospace body).
    public static void Export(string reportText, string path)
        => WritePackage(path, BuildDocumentXmlFromText(reportText ?? ""));

    // ----- package plumbing ------------------------------------------------

    private static void WritePackage(string path, string documentXml)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        WriteEntry(zip, "[Content_Types].xml", ContentTypesXml);
        WriteEntry(zip, "_rels/.rels", RelsXml);
        WriteEntry(zip, "word/_rels/document.xml.rels", DocRelsXml);
        WriteEntry(zip, "word/styles.xml", StylesXml);
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
        "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
        "</Types>";

    private const string RelsXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
        "</Relationships>";

    private const string DocRelsXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
        "</Relationships>";

    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private const string StylesXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<w:styles xmlns:w=\"" + W + "\">" +
        "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\"/><w:sz w:val=\"22\"/></w:rPr></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Title\"><w:name w:val=\"Title\"/><w:pPr><w:spacing w:after=\"200\"/></w:pPr><w:rPr><w:b/><w:sz w:val=\"44\"/><w:color w:val=\"1F3864\"/></w:rPr></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Heading1\"><w:name w:val=\"heading 1\"/><w:pPr><w:spacing w:before=\"240\" w:after=\"80\"/><w:outlineLvl w:val=\"0\"/></w:pPr><w:rPr><w:b/><w:sz w:val=\"30\"/><w:color w:val=\"1F3864\"/></w:rPr></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Heading2\"><w:name w:val=\"heading 2\"/><w:pPr><w:spacing w:before=\"160\" w:after=\"60\"/><w:outlineLvl w:val=\"1\"/></w:pPr><w:rPr><w:b/><w:sz w:val=\"26\"/><w:color w:val=\"2E5496\"/></w:rPr></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Mono\"><w:name w:val=\"Mono\"/><w:pPr><w:spacing w:after=\"0\"/></w:pPr><w:rPr><w:rFonts w:ascii=\"Courier New\" w:hAnsi=\"Courier New\"/><w:sz w:val=\"16\"/></w:rPr></w:style>" +
        "</w:styles>";

    // ----- rich renderer (structured blocks) -------------------------------

    private static string BuildDocumentXmlFromBlocks(ResearchLabReportBuilderResult result)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<w:document xmlns:w=\"").Append(W).Append("\"><w:body>");

        foreach (var b in result?.Blocks ?? new List<ReportBlock>())
        {
            switch (b.Kind)
            {
                case ReportBlockKind.Heading1: sb.Append(StyledPara("Title", b.Text)); break;
                case ReportBlockKind.Heading2: sb.Append(StyledPara("Heading1", b.Text)); break;
                case ReportBlockKind.Heading3: sb.Append(StyledPara("Heading2", b.Text)); break;
                case ReportBlockKind.Paragraph: sb.Append(NormalPara(b.Text, italic: false)); break;
                case ReportBlockKind.Note: sb.Append(NormalPara(b.Text, italic: true)); break;
                case ReportBlockKind.Callout: sb.Append(NormalPara(b.Text, italic: false, bold: true)); break;
                case ReportBlockKind.KeyValues: sb.Append(KeyValueTable(b.Rows)); break;
                case ReportBlockKind.BulletList:
                    foreach (var it in b.Rows) sb.Append(NormalPara("•  " + (it.Length > 0 ? it[0] : ""), false));
                    break;
                case ReportBlockKind.CodeBlock: sb.Append(CodeBlock(b.Text, b.CodeText)); break;
                case ReportBlockKind.Table: sb.Append(RenderTable(b)); break;
            }
        }

        sb.Append("<w:sectPr><w:pgSz w:w=\"12240\" w:h=\"15840\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\"/></w:sectPr>");
        sb.Append("</w:body></w:document>");
        return sb.ToString();
    }

    private static string RenderTable(ReportBlock b)
    {
        var cols = b.Columns;
        var rows = b.Rows;

        // Researcher-facing default: drop system columns from the dataset columns
        // table and note which were omitted.
        string omittedNote = "";
        if (b.Role == "dataset-columns" && cols.Count > 0)
        {
            var omitted = rows.Where(r => r.Length > 0 && ReportExportColumns.IsSystem(r[0]))
                              .Select(r => r[0].Trim()).Distinct().ToList();
            if (omitted.Count > 0)
            {
                rows = rows.Where(r => !(r.Length > 0 && ReportExportColumns.IsSystem(r[0]))).ToList();
                omittedNote = NormalPara(
                    "System columns were detected and omitted from this researcher-facing export: "
                    + string.Join(", ", omitted) + ".", italic: true);
            }
        }
        return DataTable(cols, rows) + omittedNote;
    }

    // ----- Word element builders -------------------------------------------

    private static string StyledPara(string styleId, string text) =>
        "<w:p><w:pPr><w:pStyle w:val=\"" + styleId + "\"/></w:pPr>" +
        "<w:r><w:t xml:space=\"preserve\">" + Esc(text) + "</w:t></w:r></w:p>";

    private static string NormalPara(string text, bool italic, bool bold = false)
    {
        if (string.IsNullOrEmpty(text)) return "<w:p/>";
        string rpr = (bold ? "<w:b/>" : "") + (italic ? "<w:i/>" : "");
        string rprEl = rpr.Length > 0 ? "<w:rPr>" + rpr + "</w:rPr>" : "";
        return "<w:p><w:r>" + rprEl + "<w:t xml:space=\"preserve\">" + Esc(text) + "</w:t></w:r></w:p>";
    }

    private static string CodeBlock(string caption, string body)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(caption))
            sb.Append("<w:p><w:r><w:rPr><w:i/><w:sz w:val=\"18\"/></w:rPr><w:t xml:space=\"preserve\">" + Esc(caption) + "</w:t></w:r></w:p>");
        foreach (var line in (body ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
            sb.Append("<w:p><w:pPr><w:pStyle w:val=\"Mono\"/></w:pPr><w:r><w:rPr><w:rFonts w:ascii=\"Courier New\" w:hAnsi=\"Courier New\"/><w:sz w:val=\"16\"/></w:rPr><w:t xml:space=\"preserve\">" + Esc(line) + "</w:t></w:r></w:p>");
        return sb.ToString();
    }

    private static string KeyValueTable(List<string[]> rows)
    {
        int labelW = 2800, valueW = PageWidthTwips - 2800;
        var sb = new StringBuilder();
        sb.Append("<w:tbl><w:tblPr><w:tblW w:w=\"").Append(PageWidthTwips).Append("\" w:type=\"dxa\"/>").Append(Borders()).Append("</w:tblPr>");
        sb.Append("<w:tblGrid><w:gridCol w:w=\"").Append(labelW).Append("\"/><w:gridCol w:w=\"").Append(valueW).Append("\"/></w:tblGrid>");
        foreach (var kv in rows)
        {
            string label = kv.Length > 0 ? kv[0] : "";
            string value = kv.Length > 1 ? kv[1] : "";
            sb.Append("<w:tr>");
            sb.Append(Cell(labelW, Esc(label), bold: true, sz: 20, shade: "F7F7F7"));
            sb.Append(Cell(valueW, Esc(value), bold: false, sz: 20, shade: null));
            sb.Append("</w:tr>");
        }
        sb.Append("</w:tbl>").Append(SpacerPara());
        return sb.ToString();
    }

    private static string DataTable(List<string> columns, List<string[]> rows)
    {
        int n = Math.Max(1, columns.Count);
        int colW = PageWidthTwips / n;
        var sb = new StringBuilder();
        sb.Append("<w:tbl><w:tblPr><w:tblW w:w=\"").Append(PageWidthTwips).Append("\" w:type=\"dxa\"/><w:tblLayout w:type=\"fixed\"/>").Append(Borders()).Append("</w:tblPr>");
        sb.Append("<w:tblGrid>");
        for (int c = 0; c < n; c++) sb.Append("<w:gridCol w:w=\"").Append(colW).Append("\"/>");
        sb.Append("</w:tblGrid>");

        // header row
        sb.Append("<w:tr><w:trPr><w:tblHeader/></w:trPr>");
        for (int c = 0; c < n; c++) sb.Append(Cell(colW, Esc(columns[c]), bold: true, sz: 16, shade: "F2F2F2"));
        sb.Append("</w:tr>");

        foreach (var row in rows)
        {
            sb.Append("<w:tr>");
            for (int c = 0; c < n; c++) sb.Append(Cell(colW, Esc(c < row.Length ? row[c] : ""), bold: false, sz: 16, shade: null));
            sb.Append("</w:tr>");
        }
        sb.Append("</w:tbl>").Append(SpacerPara());
        return sb.ToString();
    }

    private static string Cell(int width, string escapedText, bool bold, int sz, string? shade)
    {
        string shd = shade != null ? "<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"" + shade + "\"/>" : "";
        string rpr = "<w:rPr>" + (bold ? "<w:b/>" : "") + "<w:sz w:val=\"" + sz + "\"/></w:rPr>";
        return "<w:tc><w:tcPr><w:tcW w:w=\"" + width + "\" w:type=\"dxa\"/>" + shd + "</w:tcPr>" +
               "<w:p><w:pPr><w:spacing w:after=\"0\"/></w:pPr><w:r>" + rpr +
               "<w:t xml:space=\"preserve\">" + escapedText + "</w:t></w:r></w:p></w:tc>";
    }

    private static string Borders() =>
        "<w:tblBorders>" +
        "<w:top w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"BFBFBF\"/>" +
        "<w:left w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"BFBFBF\"/>" +
        "<w:bottom w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"BFBFBF\"/>" +
        "<w:right w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"BFBFBF\"/>" +
        "<w:insideH w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"D9D9D9\"/>" +
        "<w:insideV w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"D9D9D9\"/>" +
        "</w:tblBorders>";

    private static string SpacerPara() => "<w:p><w:pPr><w:spacing w:after=\"0\"/></w:pPr></w:p>";

    // ----- simple text renderer (back-compat string overload) --------------

    private static string BuildDocumentXmlFromText(string text)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<w:document xmlns:w=\"").Append(W).Append("\"><w:body>");
        string[] lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string next = i + 1 < lines.Length ? lines[i + 1] : "";
            if (line.Length > 0 && IsUnderline(next, '=')) { sb.Append(StyledPara("Title", line)); i++; }
            else if (line.Length > 0 && IsUnderline(next, '-')) { sb.Append(StyledPara("Heading1", line)); i++; }
            else if (line.Length == 0) sb.Append("<w:p/>");
            else sb.Append("<w:p><w:pPr><w:pStyle w:val=\"Mono\"/></w:pPr><w:r><w:rPr><w:rFonts w:ascii=\"Courier New\" w:hAnsi=\"Courier New\"/><w:sz w:val=\"18\"/></w:rPr><w:t xml:space=\"preserve\">" + Esc(line) + "</w:t></w:r></w:p>");
        }
        sb.Append("<w:sectPr><w:pgSz w:w=\"12240\" w:h=\"15840\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\"/></w:sectPr>");
        sb.Append("</w:body></w:document>");
        return sb.ToString();
    }

    private static bool IsUnderline(string s, char c)
    {
        if (s.Length < 3) return false;
        foreach (char ch in s) if (ch != c) return false;
        return true;
    }

    private static string Esc(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                default:
                    if (c < 0x20 && c != '\t') sb.Append(' ');
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
