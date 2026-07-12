using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AIFlashcardMaker;

// ---------------------------------------------------------------------------
// Research Lab — deterministic PDF exporter for the Report Builder.
//
// Renders the structured report blocks into a researcher-facing PDF (1.4) that
// visually mirrors the DOCX: a large dark-blue title, dark-blue section headings,
// and REAL drawn tables (gray header rows, visible borders, wrapped cells) for the
// overview / study-design / dataset / variables / descriptive / result sections —
// not monospace text dumps. Base-14 fonts only (Helvetica / Helvetica-Bold /
// Helvetica-Oblique for prose+headings, Courier for aggregate/technical blocks) —
// no embedding, no external NuGet package. Non-ASCII is transliterated to ASCII
// (numbers never change). System dataset columns (Sample_ID/Sample_Type/Timestamp/
// Username) are omitted from the columns table with a note. Deterministic:
// identical input → identical bytes (no timestamps).
// ---------------------------------------------------------------------------
public static class ResearchLabPdfExporter
{
    public static void Export(ResearchLabReportBuilderResult result, string path)
        => File.WriteAllBytes(path, Build(result));

    public static void Export(string reportText, string path)
        => File.WriteAllBytes(path, Build(reportText ?? ""));

    // Colors (kept as exact literal strings so drawn-table evidence is stable).
    private const string Blue = "0.122 0.216 0.392";
    private const string Black = "0 0 0";
    private const string NoteGray = "0.4 0.4 0.4";
    private const string HeaderFill = "0.95 0.95 0.95";
    private const string LabelFill = "0.97 0.97 0.97";
    private const string Border = "0.75 0.75 0.75";

    // Fonts: 1=Courier, 2=Helvetica, 3=Helvetica-Bold, 4=Helvetica-Oblique.
    private const int Courier = 1, Helv = 2, HelvB = 3, HelvI = 4;

    // ---- rich renderer (structured blocks) --------------------------------

    public static byte[] Build(ResearchLabReportBuilderResult result)
    {
        var painter = new Painter();
        foreach (var b in result?.Blocks ?? new List<ReportBlock>())
        {
            switch (b.Kind)
            {
                case ReportBlockKind.Heading1: painter.Title(b.Text); break;
                case ReportBlockKind.Heading2: painter.Heading(b.Text, 13); break;
                case ReportBlockKind.Heading3: painter.Heading(b.Text, 11); break;
                case ReportBlockKind.Paragraph: painter.Paragraph(b.Text, Helv, 10, Black); break;
                case ReportBlockKind.Note: painter.Paragraph(b.Text, HelvI, 9, NoteGray); break;
                case ReportBlockKind.Callout: painter.Paragraph(b.Text, HelvB, 10, Blue); break;
                case ReportBlockKind.BulletList:
                    foreach (var it in b.Rows) painter.Paragraph("-  " + (it.Length > 0 ? it[0] : ""), Helv, 10, Black);
                    painter.VSpace(2); break;
                case ReportBlockKind.KeyValues: painter.KeyValueTable(b.Rows); break;
                case ReportBlockKind.CodeBlock: painter.CodeBlock(b.Text, b.CodeText); break;
                case ReportBlockKind.Table: painter.DataTableBlock(b); break;
            }
        }
        return Assemble(painter.Finish(), Transliterate(result?.Title ?? ""));
    }

    // ---- simple renderer (back-compat string overload) --------------------

    public static byte[] Build(string reportText)
    {
        var painter = new Painter();
        foreach (var raw in (reportText ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
            painter.MonoLine(raw);
        return Assemble(painter.Finish(), "");
    }

    // ---- page painter -----------------------------------------------------

    private sealed class Painter
    {
        private const double PW = 612, ML = 50, MR = 50, MT = 742, MB = 56;
        private const double CW = PW - ML - MR;   // 512
        private const double Pad = 3;

        private readonly List<string> _pages = new();
        private StringBuilder _b = new();
        private double _y = MT;

        public List<string> Finish() { _pages.Add(_b.ToString()); return _pages; }

        private void NewPage() { _pages.Add(_b.ToString()); _b = new StringBuilder(); _y = MT; }
        public void VSpace(double h) { _y -= h; if (_y < MB) NewPage(); }

        private void Ensure(double h) { if (_y - h < MB && _y < MT - 0.1) NewPage(); }

        private static int CharsFor(double width, double size) => Math.Max(1, (int)((width) / (0.52 * size)));

        // ----- text primitives -----

        private void Text(double x, double baseline, string transliterated, int font, double size, string color)
        {
            _b.Append(color).Append(" rg\nBT /F").Append(font).Append(' ').Append(Num(size))
              .Append(" Tf 1 0 0 1 ").Append(Num(x)).Append(' ').Append(Num(baseline)).Append(" Tm (")
              .Append(EscapePdf(transliterated)).Append(") Tj ET\n");
        }

        public void Title(string text)
        {
            string t = Transliterate(text);
            VSpace(2); Ensure(24);
            double by = _y - 18;
            Text(ML, by, t, HelvB, 18, Blue);
            _y = by - 6;
            // thin rule under the title
            Rule(_y + 2);
            _y -= 6;
        }

        public void Heading(string text, double size)
        {
            string t = Transliterate(text);
            VSpace(6); Ensure(size + 6);
            double by = _y - size;
            Text(ML, by, t, HelvB, size, Blue);
            _y = by - 4;
        }

        public void Paragraph(string text, int font, double size, string color)
        {
            string t = Transliterate(text);
            foreach (var line in WordWrap(t, CharsFor(CW, size)))
            {
                Ensure(size + 3);
                double by = _y - size;
                Text(ML, by, line, font, size, color);
                _y = by - 3;
            }
            _y -= 3;
        }

        public void MonoLine(string raw)
        {
            foreach (var line in WordWrap(Transliterate(raw).Replace("\t", "    "), 92))
            {
                Ensure(11);
                double by = _y - 9;
                Text(ML, by, line, Courier, 9, Black);
                _y = by - 2;
            }
        }

        public void CodeBlock(string caption, string body)
        {
            if (!string.IsNullOrWhiteSpace(caption)) Paragraph(caption, HelvI, 9, NoteGray);
            foreach (var cl in (body ?? "").Replace("\r", "").Split('\n'))
                foreach (var line in WordWrap(Transliterate(cl), 108))
                {
                    Ensure(10);
                    double by = _y - 8;
                    Text(ML, by, line, Courier, 8, Black);
                    _y = by - 2;
                }
            _y -= 3;
        }

        // ----- tables -----

        public void KeyValueTable(List<string[]> rows)
        {
            double[] w = { CW * 0.30, CW * 0.70 };
            foreach (var kv in rows)
                DrawRow(new[] { kv.Length > 0 ? kv[0] : "", kv.Length > 1 ? kv[1] : "" },
                        w, size: 9, boldCells: false, fill: null, boldFirstCol: true, firstColFill: LabelFill);
            _y -= 4;
        }

        public void DataTableBlock(ReportBlock b)
        {
            var cols = b.Columns.Select(Transliterate).ToList();
            var rows = b.Rows.Select(r => r.Select(Transliterate).ToArray()).ToList();

            string note = "";
            if (b.Role == "dataset-columns" && cols.Count > 0)
            {
                var omit = rows.Where(r => r.Length > 0 && ReportExportColumns.IsSystem(r[0]))
                               .Select(r => r[0].Trim()).Distinct().ToList();
                if (omit.Count > 0)
                {
                    rows = rows.Where(r => !(r.Length > 0 && ReportExportColumns.IsSystem(r[0]))).ToList();
                    note = "System columns were detected and omitted from this researcher-facing export: " + string.Join(", ", omit) + ".";
                }
            }

            double[] w = ColumnWidths(b.Role, cols.Count);
            // header row
            DrawRow(cols.ToArray(), w, size: 8, boldCells: true, fill: HeaderFill, boldFirstCol: false, firstColFill: null);
            foreach (var r in rows)
                DrawRow(r, w, size: 8, boldCells: false, fill: null, boldFirstCol: false, firstColFill: null);
            _y -= 3;
            if (note.Length > 0) Paragraph(note, HelvI, 8, NoteGray);
        }

        private static double[] ColumnWidths(string role, int n)
        {
            double[] ratios =
                role == "dataset-columns" && n == 4 ? new[] { 0.46, 0.24, 0.15, 0.15 } :
                role == "variables" && n == 5 ? new[] { 0.22, 0.14, 0.14, 0.14, 0.36 } :
                Enumerable.Repeat(1.0 / Math.Max(1, n), Math.Max(1, n)).ToArray();
            return ratios.Select(x => x * CW).ToArray();
        }

        private void DrawRow(string[] cells, double[] colW, double size, bool boldCells, string? fill, bool boldFirstCol, string? firstColFill)
        {
            int n = colW.Length;
            var wrapped = new List<string>[n];
            int maxLines = 1;
            for (int c = 0; c < n; c++)
            {
                string cell = c < cells.Length ? cells[c] : "";
                wrapped[c] = WordWrap(cell, CharsFor(colW[c] - 2 * Pad, size));
                maxLines = Math.Max(maxLines, wrapped[c].Count);
            }
            double lineStep = size + 2;
            double rowH = maxLines * lineStep + 2 * Pad;

            Ensure(rowH);
            double top = _y, bottom = _y - rowH;

            // fills
            if (fill != null) FillRect(ML, bottom, CW, rowH, fill);
            if (firstColFill != null) FillRect(ML, bottom, colW[0], rowH, firstColFill);

            // borders (outer box + inner verticals)
            StrokeRect(ML, bottom, CW, rowH);
            double x = ML;
            for (int c = 0; c < n; c++)
            {
                if (c > 0) Line(x, top, x, bottom);
                double tx = x + Pad, ty = top - Pad - size * 0.82;
                bool bold = boldCells || (boldFirstCol && c == 0);
                foreach (var ln in wrapped[c])
                {
                    Text(tx, ty, ln, bold ? HelvB : Helv, size, Black);
                    ty -= lineStep;
                }
                x += colW[c];
            }
            _y = bottom;
        }

        // ----- vector helpers -----

        private void Rule(double y) =>
            _b.Append(Border).Append(" RG 0.6 w\n").Append(Num(ML)).Append(' ').Append(Num(y))
              .Append(" m ").Append(Num(ML + CW)).Append(' ').Append(Num(y)).Append(" l S\n");

        private void FillRect(double x, double y, double w, double h, string color) =>
            _b.Append(color).Append(" rg\n").Append(Num(x)).Append(' ').Append(Num(y)).Append(' ')
              .Append(Num(w)).Append(' ').Append(Num(h)).Append(" re\nf\n");

        private void StrokeRect(double x, double y, double w, double h) =>
            _b.Append(Border).Append(" RG 0.6 w\n").Append(Num(x)).Append(' ').Append(Num(y)).Append(' ')
              .Append(Num(w)).Append(' ').Append(Num(h)).Append(" re\nS\n");

        private void Line(double x1, double y1, double x2, double y2) =>
            _b.Append(Border).Append(" RG 0.6 w\n").Append(Num(x1)).Append(' ').Append(Num(y1))
              .Append(" m ").Append(Num(x2)).Append(' ').Append(Num(y2)).Append(" l S\n");
    }

    // ---- object assembly + header/footer ----------------------------------

    private static byte[] Assemble(List<string> pageBodies, string headerTitle)
    {
        int numPages = Math.Max(1, pageBodies.Count);
        int PageObj(int i) => 7 + i;
        int ContentObj(int i) => 7 + numPages + i;
        int objCount = 6 + 2 * numPages;

        var ms = new MemoryStream();
        var offsets = new long[objCount + 1];
        void Write(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
        void Obj(int num, string body) { offsets[num] = ms.Position; Write($"{num} 0 obj\n{body}\nendobj\n"); }

        Write("%PDF-1.4\n");
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        var kids = new StringBuilder();
        for (int i = 0; i < numPages; i++) kids.Append(PageObj(i)).Append(" 0 R ");
        Obj(2, $"<< /Type /Pages /Kids [{kids.ToString().Trim()}] /Count {numPages} >>");
        Obj(3, "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>");
        Obj(4, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>");
        Obj(6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Oblique >>");

        string headerT = Truncate(headerTitle, 95);
        for (int i = 0; i < numPages; i++)
        {
            Obj(PageObj(i),
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Resources << /Font << /F1 3 0 R /F2 4 0 R /F3 5 0 R /F4 6 0 R >> >> " +
                $"/Contents {ContentObj(i)} 0 R >>");

            var c = new StringBuilder();
            // running header
            if (headerT.Length > 0)
                c.Append("0.4 0.4 0.4 rg\nBT /F2 8 Tf 1 0 0 1 50 766 Tm (").Append(EscapePdf(headerT)).Append(") Tj ET\n");
            // body (already-built graphics + text operators)
            c.Append(i < pageBodies.Count ? pageBodies[i] : "");
            // page-number footer, roughly centered
            string foot = $"Page {i + 1} of {numPages}";
            double footX = (612 - foot.Length * 4.4) / 2.0;
            c.Append("0.4 0.4 0.4 rg\nBT /F2 8 Tf 1 0 0 1 ").Append(Num(footX)).Append(" 34 Tm (").Append(EscapePdf(foot)).Append(") Tj ET\n");

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

    // ---- shared helpers ---------------------------------------------------

    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + ".";

    private static List<string> WordWrap(string s, int maxChars)
    {
        var outp = new List<string>();
        if (maxChars < 1) maxChars = 1;
        foreach (var rawLine in (s ?? "").Split('\n'))
        {
            string cur = "";
            foreach (var raw in rawLine.Split(' '))
            {
                string word = raw;
                while (word.Length > maxChars)
                {
                    if (cur.Length > 0) { outp.Add(cur); cur = ""; }
                    outp.Add(word.Substring(0, maxChars));
                    word = word.Substring(maxChars);
                }
                if (cur.Length == 0) cur = word;
                else if (cur.Length + 1 + word.Length <= maxChars) cur += " " + word;
                else { outp.Add(cur); cur = word; }
            }
            outp.Add(cur);
        }
        if (outp.Count == 0) outp.Add("");
        return outp;
    }

    private static string EscapePdf(string s)
        => (s ?? "").Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    private static string Transliterate(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            switch (c)
            {
                case '·': sb.Append('-'); break;
                case '•': sb.Append('-'); break;
                case '—': case '–': sb.Append('-'); break;
                case '×': sb.Append('x'); break;
                case '÷': sb.Append('/'); break;
                case '“': case '”': sb.Append('"'); break;
                case '‘': case '’': sb.Append('\''); break;
                case '…': sb.Append("..."); break;
                case '²': sb.Append('2'); break;
                case '³': sb.Append('3'); break;
                case '−': sb.Append('-'); break;
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
                case '⚠': sb.Append('!'); break;
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
