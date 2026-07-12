using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AIFlashcardMaker;

// ---------------------------------------------------------------------------
// Research Lab — deterministic PDF exporter for the Report Builder.
//
// Writes a minimal, valid PDF (1.4) from the report's plain text using ONLY the
// base-14 Courier font (no font embedding, no external NuGet package). Courier is
// monospace so the report's aligned tables stay readable. Non-ASCII characters
// (·, —, ×, curly quotes, ρ, ², √, ≥, …) are transliterated to safe ASCII so the
// base font renders them correctly — numbers/digits are ASCII and never change.
//
// No AI, no network, no new statistics. It only paginates already-composed text
// (aggregate-only) that the report builder produced. Deterministic: identical
// input → identical bytes (no timestamps are written).
// ---------------------------------------------------------------------------
public static class ResearchLabPdfExporter
{
    public static void Export(string reportText, string path)
        => File.WriteAllBytes(path, Build(reportText ?? ""));

    public static byte[] Build(string reportText)
    {
        const int fontSize = 9;
        const int leading = 11;
        const int startY = 742;      // top text baseline (Letter 612x792, ~50pt top margin)
        const int leftX = 50;
        const int maxChars = 92;     // ~fits within a 512pt text column at Courier 9pt
        const int linesPerPage = 58;

        // 1) Normalize -> transliterate -> hard-wrap -> paginate.
        var raw = reportText.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var wrapped = new List<string>();
        foreach (var line in raw)
        {
            string t = Transliterate(line).Replace("\t", "    ");
            if (t.Length == 0) { wrapped.Add(""); continue; }
            for (int i = 0; i < t.Length; i += maxChars)
                wrapped.Add(t.Substring(i, Math.Min(maxChars, t.Length - i)));
        }
        var pages = new List<List<string>>();
        for (int i = 0; i < wrapped.Count; i += linesPerPage)
            pages.Add(wrapped.GetRange(i, Math.Min(linesPerPage, wrapped.Count - i)));
        if (pages.Count == 0) pages.Add(new List<string> { "" });

        int numPages = pages.Count;
        int objCount = 3 + 2 * numPages;                 // catalog, pages, font, + (page + content) each
        int PageObj(int i) => 4 + i;
        int ContentObj(int i) => 4 + numPages + i;

        var ms = new MemoryStream();
        var offsets = new long[objCount + 1];            // 1-based
        void Write(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
        void Obj(int num, string body) { offsets[num] = ms.Position; Write($"{num} 0 obj\n{body}\nendobj\n"); }

        Write("%PDF-1.4\n");

        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");

        var kids = new StringBuilder();
        for (int i = 0; i < numPages; i++) kids.Append(PageObj(i)).Append(" 0 R ");
        Obj(2, $"<< /Type /Pages /Kids [{kids.ToString().Trim()}] /Count {numPages} >>");

        Obj(3, "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>");

        for (int i = 0; i < numPages; i++)
        {
            Obj(PageObj(i),
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Resources << /Font << /F1 3 0 R >> >> " +
                $"/Contents {ContentObj(i)} 0 R >>");

            var c = new StringBuilder();
            c.Append("BT /F1 ").Append(fontSize).Append(" Tf ")
             .Append(leftX).Append(' ').Append(startY).Append(" Td ")
             .Append(leading).Append(" TL\n");
            foreach (var ln in pages[i])
                c.Append('(').Append(EscapePdf(ln)).Append(") Tj T*\n");
            c.Append("ET");

            string content = c.ToString();
            int len = Encoding.ASCII.GetByteCount(content);
            Obj(ContentObj(i), $"<< /Length {len} >>\nstream\n{content}\nendstream");
        }

        long xrefStart = ms.Position;
        Write("xref\n");
        Write($"0 {objCount + 1}\n");
        Write("0000000000 65535 f \n");
        for (int num = 1; num <= objCount; num++)
            Write(offsets[num].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Write($"trailer\n<< /Size {objCount + 1} /Root 1 0 R >>\n");
        Write($"startxref\n{xrefStart}\n%%EOF\n");

        return ms.ToArray();
    }

    private static string EscapePdf(string s)
        => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    // Map the non-ASCII characters the report actually uses to safe ASCII so the
    // base Courier font renders them. Anything else non-printable-ASCII becomes '?'
    // (rare). Digits and effect/number text are ASCII and pass through unchanged.
    private static string Transliterate(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            switch (c)
            {
                case '·': sb.Append('-'); break;                 // middle dot separator
                case '—': case '–': sb.Append('-'); break;      // em/en dash
                case '×': sb.Append('x'); break;
                case '÷': sb.Append('/'); break;
                case '“': case '”': sb.Append('"'); break;
                case '‘': case '’': sb.Append('\''); break;
                case '…': sb.Append("..."); break;
                case '²': sb.Append('2'); break;
                case '³': sb.Append('3'); break;
                case '−': sb.Append('-'); break;                 // minus sign
                case '±': sb.Append("+/-"); break;
                case '≥': sb.Append(">="); break;
                case '≤': sb.Append("<="); break;
                case '≠': sb.Append("!="); break;
                case '→': sb.Append("->"); break;
                case '√': sb.Append("sqrt"); break;
                case 'ρ': sb.Append("rho"); break;
                case 'η': sb.Append("eta"); break;
                case 'φ': sb.Append("phi"); break;
                case 'χ': sb.Append("chi"); break;
                case 'µ': case 'μ': sb.Append("mu"); break;
                case 'σ': sb.Append("sigma"); break;
                case '½': sb.Append("1/2"); break;
                default:
                    if (c >= 0x20 && c <= 0x7E) sb.Append(c);
                    else if (c == '\t') sb.Append("    ");
                    else sb.Append('?');
                    break;
            }
        }
        return sb.ToString();
    }
}
