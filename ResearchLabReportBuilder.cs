using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AIFlashcardMaker;

// ---------------------------------------------------------------------------
// Research Lab — Report Builder (MVP).
//
// Deterministic, local, AGGREGATE-ONLY. Assembles data the project ALREADY has —
// metadata, extraction variables, dataset summary, descriptive statistics, and
// computed results (plus optional CURRENT-SESSION manuscript narratives) — into a
// clean TXT + Markdown report draft.
//
// Hard guarantees (mirrored by tests + a source scan):
//   * No AI, no network/API calls, no Z.ai, no auth/payment.
//   * No new statistics and no formula changes — it only reads already-computed
//     values and calls existing public builders.
//   * No raw participant rows and no participant identifiers. The dataset summary
//     deliberately omits CsvColumnSummary.SampleValues (which could contain an id
//     column's example values). Every result's raw block comes from the engine's
//     own aggregate ToPlainText() (contingency/group/rank tables only).
//
// Manuscript Methods/Results prose needs a live typed result (IInferenceExportable)
// which is session-only. The UI passes in the narratives it can generate this
// session (keyed by SavedComputedResult.Id, which equals ComputedResultRow.Id —
// preserved by ToSaved/FromSaved). For any result without a session narrative the
// report shows a clear Re-run prompt and still includes the saved aggregate output.
// ---------------------------------------------------------------------------

public sealed class ResearchLabReportBuilderOptions
{
    // Current statistics fingerprint (StatisticsFingerprint.Compute) so the
    // composer can flag stale descriptive/computed sections. When null, staleness
    // is NOT asserted — the composer never guesses "fresh" or "stale" blindly.
    public string? CurrentFingerprint { get; set; }

    public bool IncludeDescriptiveStatistics { get; set; } = true;
    public bool IncludeComputedResults { get; set; } = true;
}

public sealed class ResearchLabReportBuilderResult
{
    public string Title { get; set; } = "";
    public string TextReport { get; set; } = "";
    public string MarkdownReport { get; set; } = "";

    // Metadata only — deliberately NOT written into the report body, so the same
    // project produces byte-identical TextReport/MarkdownReport on every build.
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    public bool IsDeterministic { get; set; } = true;
    public bool AiUsed { get; set; }

    public List<string> Warnings { get; set; } = new();
    public int IncludedResultCount { get; set; }
    public int RerunNeededCount { get; set; }
    public int StaleResultCount { get; set; }
}

public static class ResearchLabReportBuilder
{
    public const string DeterministicFooter =
        "Generated deterministically from aggregate computed results on this device. "
        + "No AI was used. No participant-level rows or identifiers are included.";

    private const string RerunPrompt =
        "Re-run this analysis to include manuscript-ready Methods and Results text.";

    public static ResearchLabReportBuilderResult Build(
        ResearchProject project,
        IReadOnlyDictionary<string, ResearchLabNarrativeResult>? sessionNarratives = null,
        ResearchLabReportBuilderOptions? options = null)
    {
        options ??= new ResearchLabReportBuilderOptions();
        var narratives = sessionNarratives ?? new Dictionary<string, ResearchLabNarrativeResult>();
        var result = new ResearchLabReportBuilderResult { GeneratedAt = DateTime.UtcNow };

        if (project is null)
        {
            result.Warnings.Add("No project was provided, so an empty report was produced.");
            result.TextReport = "No research project is open.\n\n" + DeterministicFooter + "\n";
            result.MarkdownReport = "# No research project is open\n\n" + DeterministicFooter + "\n";
            return result;
        }

        string title = string.IsNullOrWhiteSpace(project.Title) ? "Untitled research project" : project.Title.Trim();
        result.Title = title;

        var w = new ReportWriter();
        string? curFp = options.CurrentFingerprint;

        // ---- Report-level stale banner (kept near the top for visibility) -----
        bool descStale = project.DescriptiveStatistics is { } dr && IsStale(dr.SourceFingerprint, curFp);
        var computed = options.IncludeComputedResults
            ? (project.ComputedResults ?? new List<SavedComputedResult>())
            : new List<SavedComputedResult>();
        int staleResults = computed.Count(r => IsStale(r.AnalysisFingerprint, curFp));
        result.StaleResultCount = staleResults;
        bool anyStale = descStale || staleResults > 0;

        // ---- 1. Title / project overview -------------------------------------
        w.H1(title);
        if (anyStale)
        {
            w.Callout("Some included content may be stale because the dataset or extraction "
                + "sheet changed after it was computed. Re-run the affected analyses before "
                + "using this draft in a manuscript.");
            result.Warnings.Add("Some included content may be stale (dataset or extraction sheet changed after it was computed).");
        }
        w.H2("Project overview");
        w.KeyVal("Title", title);
        w.KeyVal("Specialty", Fallback(project.Specialty, "Not specified"));
        w.KeyVal("Study type", Fallback(project.StudyType, "Not specified"));
        w.KeyVal("Current stage", Fallback(project.CurrentStage, "—"));
        w.KeyVal("Created", project.CreatedAt.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.InvariantCulture));
        w.KeyVal("Last updated", project.UpdatedAt.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.InvariantCulture));

        // ---- 2. Study design --------------------------------------------------
        w.H2("Study design");
        w.KeyVal("Aim", Fallback(project.Aim, "Not specified"));
        w.KeyVal("Population", Fallback(project.Population, "Not specified"));
        w.KeyVal("Setting", Fallback(project.Setting, "Not specified"));
        w.KeyVal("Time period", Fallback(project.TimePeriod, "Not specified"));
        w.KeyVal("Available data", Fallback(project.AvailableDataType, "Not specified"));
        var outputs = (project.DesiredOutputs ?? new List<string>()).Where(o => !string.IsNullOrWhiteSpace(o)).ToList();
        w.KeyVal("Desired outputs", outputs.Count > 0 ? string.Join(", ", outputs) : "Not specified");

        // ---- 3. Dataset summary ----------------------------------------------
        w.H2("Dataset summary");
        var csv = project.CsvSampleSummary;
        if (csv is null || csv.Columns.Count == 0)
        {
            w.Para("No dataset sample has been imported yet. Import a CSV sample in Data "
                + "Extraction to include a dataset summary.");
            result.Warnings.Add("No dataset sample imported.");
        }
        else
        {
            w.KeyVal("File", Fallback(csv.FileName, "—"));
            w.KeyVal("Rows", csv.TotalRows.ToString(CultureInfo.InvariantCulture));
            w.KeyVal("Columns", csv.Columns.Count.ToString(CultureInfo.InvariantCulture));
            if (project.TargetSampleSize is { } tss)
            {
                w.KeyVal("Target sample size", tss.ToString(CultureInfo.InvariantCulture)
                    + (csv.TotalRows < tss ? "  (imported sample is smaller than the stated target)" : ""));
            }
            w.Blank();
            // Aggregate column metadata only — SampleValues are intentionally omitted.
            w.Table(
                new[] { "Column", "Inferred type", "Unique", "Missing %" },
                csv.Columns.Select(c => new[]
                {
                    Safe(c.Name),
                    Fallback(c.InferredType, "—"),
                    c.UniqueCount.ToString(CultureInfo.InvariantCulture),
                    c.MissingPercent.ToString(CultureInfo.InvariantCulture) + "%"
                }).ToList());
            w.Note("Column metadata only. Example cell values are intentionally excluded so no participant-level data appears in this report.");
        }

        // ---- 4. Variables summary --------------------------------------------
        w.H2("Variables summary");
        var vars = (project.Variables ?? new List<ResearchVariable>()).ToList();
        if (vars.Count == 0)
        {
            w.Para("No extraction variables have been defined yet. Build the extraction sheet "
                + "in Data Extraction to include a variables summary.");
            result.Warnings.Add("No extraction variables defined.");
        }
        else
        {
            w.Table(
                new[] { "Variable", "Type", "Level", "Role", "Coding / labels" },
                vars.Select(v => new[]
                {
                    Safe(v.VariableName),
                    Fallback(v.VariableType, "—"),
                    Fallback(v.MeasurementLevel, "—"),
                    Fallback(v.Role, "—"),
                    CodingSummary(v)
                }).ToList());
        }

        // ---- 5. Descriptive statistics summary -------------------------------
        w.H2("Descriptive statistics summary");
        if (!options.IncludeDescriptiveStatistics)
        {
            w.Para("Descriptive statistics were excluded from this report.");
        }
        else if (project.DescriptiveStatistics is not { } rec)
        {
            w.Para("Run descriptive statistics to include this section.");
            result.Warnings.Add("No descriptive statistics have been generated.");
        }
        else
        {
            if (descStale)
                w.Note("These descriptive statistics may be stale — the dataset or extraction sheet changed after they were generated. Re-run descriptive statistics to refresh them.");
            w.KeyVal("Rows analyzed", rec.RowsAnalyzed.ToString(CultureInfo.InvariantCulture));
            w.KeyVal("Variables analyzed", rec.VariablesAnalyzed.ToString(CultureInfo.InvariantCulture));
            w.KeyVal("Overall missing", rec.OverallMissingPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%");
            w.Blank();
            try
            {
                var tables = StatisticsTableBuilder.Build(rec);
                if (tables.Count > 0)
                {
                    // Markdown gets structured tables; TXT reuses the engine's own
                    // deterministic plain-text rendering.
                    string plain = StatisticsExportService.BuildPlainText(rec, tables);
                    foreach (var tbl in tables)
                        w.MarkdownOnlyTable(tbl);
                    w.TextOnlyBlock("Descriptive statistics", plain);
                }
                else
                {
                    w.Para("Descriptive statistics were generated but produced no displayable tables.");
                }
            }
            catch (Exception ex)
            {
                w.Para("Descriptive statistics could not be rendered for this report (" + ex.Message + ").");
                result.Warnings.Add("Descriptive statistics rendering failed: " + ex.Message);
            }
        }

        // ---- 6 & 7. Methods + Results (per computed result) -------------------
        result.IncludedResultCount = computed.Count;
        int rerunNeeded = 0;

        w.H2("Statistical methods");
        if (!options.IncludeComputedResults)
            w.Para("Computed results were excluded from this report.");
        else if (computed.Count == 0)
            w.Para("Run analyses to include computed results.");
        else
        {
            foreach (var r in computed)
            {
                w.H3(ResultHeading(r));
                var nar = Lookup(narratives, r.Id);
                if (nar is not null && !string.IsNullOrWhiteSpace(nar.MethodsText))
                    w.Para(nar.MethodsText.Trim());
                else
                    w.Note(RerunPrompt);
            }
        }

        w.H2("Results");
        if (!options.IncludeComputedResults)
            w.Para("Computed results were excluded from this report.");
        else if (computed.Count == 0)
            w.Para("Run analyses to include computed results.");
        else
        {
            foreach (var r in computed)
            {
                w.H3(ResultHeading(r));
                bool stale = IsStale(r.AnalysisFingerprint, curFp);
                if (stale)
                    w.Note("This result may be stale — the dataset or extraction sheet changed after it was computed. Re-run it before using these numbers.");

                // Compact summary line from persisted display fields.
                w.KeyVal("Test", Fallback(r.TestName, "—"));
                if (!string.IsNullOrWhiteSpace(r.ValidNDisplay)) w.KeyVal("Sample", r.ValidNDisplay);
                if (!string.IsNullOrWhiteSpace(r.EffectDisplay)) w.KeyVal("Effect", r.EffectDisplay);
                if (!string.IsNullOrWhiteSpace(r.PValueDisplay)) w.KeyVal("p-value", r.PValueDisplay);
                if (!string.IsNullOrWhiteSpace(r.SignificanceText)) w.KeyVal("Significance", r.SignificanceText);
                w.Blank();

                var nar = Lookup(narratives, r.Id);
                if (nar is not null && !string.IsNullOrWhiteSpace(nar.ResultsText))
                {
                    w.Para(nar.ResultsText.Trim());
                }
                else
                {
                    rerunNeeded++;
                    w.Note(RerunPrompt);
                }

                if (!string.IsNullOrWhiteSpace(r.FullPlainText))
                    w.CodeBlock("Statistical output (aggregate) — not manuscript prose, not raw data",
                        r.FullPlainText.Trim());

                if (!string.IsNullOrWhiteSpace(r.AuditNote))
                    w.Note(r.AuditNote.Trim());
            }
        }
        result.RerunNeededCount = rerunNeeded;
        if (rerunNeeded > 0)
            result.Warnings.Add($"{rerunNeeded} computed result(s) need a Re-run to include manuscript-ready Methods and Results text.");

        // ---- 8. Notes / limitations ------------------------------------------
        w.H2("Notes and limitations");
        var noteLines = new List<string>
        {
            "This report is a deterministic draft assembled from data already computed in this project. It is not a substitute for professional statistical or clinical review.",
            "Reported analyses are crude (unadjusted) and were not adjusted for confounders.",
            "Statistical associations describe relationships only; association does not imply causation, and correlation does not imply causation.",
            "Effect-size strength labels, where shown, are heuristic descriptive bands and should not be over-interpreted."
        };
        // Fold in any distinct session-narrative Notes text (already deterministic,
        // aggregate-only) so manuscript-specific caveats are not lost.
        foreach (var r in computed)
        {
            var nar = Lookup(narratives, r.Id);
            var nt = nar?.NotesText?.Trim();
            if (!string.IsNullOrWhiteSpace(nt) && !noteLines.Contains(nt))
                noteLines.Add(nt);
        }
        w.BulletList(noteLines);

        // ---- 9. Stale warnings (explicit section) ----------------------------
        w.H2("Stale warnings");
        if (!anyStale)
            w.Para("No stale content detected in the included sections.");
        else
        {
            var staleLines = new List<string>();
            if (descStale) staleLines.Add("Descriptive statistics were generated before the most recent dataset or extraction-sheet change.");
            if (staleResults > 0) staleLines.Add($"{staleResults} computed result(s) were computed before the most recent dataset or extraction-sheet change.");
            staleLines.Add("Re-run the affected analyses to refresh them before using this draft in a manuscript.");
            w.BulletList(staleLines);
        }

        // ---- 10. Deterministic / no-AI footer --------------------------------
        w.H2("How this report was generated");
        w.Para(DeterministicFooter);

        result.TextReport = w.Text;
        result.MarkdownReport = w.Markdown;
        return result;
    }

    // ----- helpers ---------------------------------------------------------

    private static bool IsStale(string savedFingerprint, string? currentFingerprint)
        => currentFingerprint is not null
           && (string.IsNullOrEmpty(savedFingerprint)
               || !string.Equals(savedFingerprint, currentFingerprint, StringComparison.Ordinal));

    private static ResearchLabNarrativeResult? Lookup(
        IReadOnlyDictionary<string, ResearchLabNarrativeResult> map, string id)
        => !string.IsNullOrEmpty(id) && map.TryGetValue(id, out var n) ? n : null;

    private static string ResultHeading(SavedComputedResult r)
    {
        string vars = !string.IsNullOrWhiteSpace(r.Variables) ? r.Variables.Trim() : "Result";
        return string.IsNullOrWhiteSpace(r.TestName) ? vars : $"{r.TestName.Trim()} — {vars}";
    }

    private static string CodingSummary(ResearchVariable v)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(v.Coding)) parts.Add(v.Coding.Trim());
        if (!string.IsNullOrWhiteSpace(v.ValueLabels)) parts.Add(v.ValueLabels.Trim());
        return parts.Count > 0 ? string.Join(" · ", parts) : "—";
    }

    private static string Fallback(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    // -----------------------------------------------------------------------
    // Writes TXT and Markdown in a single pass so the two stay in lock-step.
    // -----------------------------------------------------------------------
    private sealed class ReportWriter
    {
        private readonly StringBuilder _t = new();
        private readonly StringBuilder _m = new();

        public string Text => _t.ToString().TrimEnd() + "\n";
        public string Markdown => _m.ToString().TrimEnd() + "\n";

        public void H1(string s)
        {
            _t.AppendLine(s.ToUpperInvariant());
            _t.AppendLine(new string('=', Math.Max(3, s.Length)));
            _t.AppendLine();
            _m.AppendLine("# " + s);
            _m.AppendLine();
        }

        public void H2(string s)
        {
            _t.AppendLine();
            _t.AppendLine(s);
            _t.AppendLine(new string('-', Math.Max(3, s.Length)));
            _m.AppendLine();
            _m.AppendLine("## " + s);
            _m.AppendLine();
        }

        public void H3(string s)
        {
            _t.AppendLine();
            _t.AppendLine(s);
            _m.AppendLine();
            _m.AppendLine("### " + s);
            _m.AppendLine();
        }

        public void Para(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            _t.AppendLine(s.Trim());
            _t.AppendLine();
            _m.AppendLine(s.Trim());
            _m.AppendLine();
        }

        // A visually distinct one-line note (prompt / caveat / audit line).
        public void Note(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            _t.AppendLine("> " + s.Trim());
            _t.AppendLine();
            _m.AppendLine("> " + s.Trim());
            _m.AppendLine();
        }

        // A stronger, boxed banner (used for the top-of-report stale warning).
        public void Callout(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            _t.AppendLine("!! " + s.Trim());
            _t.AppendLine();
            _m.AppendLine("> **⚠ " + s.Trim() + "**");
            _m.AppendLine();
        }

        public void KeyVal(string label, string value)
        {
            _t.AppendLine($"{label,-18}: {value}");
            _m.AppendLine($"- **{label}:** {value}");
        }

        public void BulletList(IEnumerable<string> items)
        {
            foreach (var it in items)
            {
                if (string.IsNullOrWhiteSpace(it)) continue;
                _t.AppendLine("  - " + it.Trim());
                _m.AppendLine("- " + it.Trim());
            }
            _t.AppendLine();
            _m.AppendLine();
        }

        public void Blank()
        {
            _t.AppendLine();
            _m.AppendLine();
        }

        // TXT: fixed-width aligned table. MD: pipe table.
        public void Table(IReadOnlyList<string> headers, List<string[]> rows)
        {
            int cols = headers.Count;
            var widths = new int[cols];
            for (int c = 0; c < cols; c++) widths[c] = headers[c].Length;
            foreach (var row in rows)
                for (int c = 0; c < cols; c++)
                    widths[c] = Math.Max(widths[c], (c < row.Length ? row[c] : "").Length);

            _t.AppendLine(RowLine(headers.ToArray(), widths));
            _t.AppendLine(string.Join("-+-", widths.Select(x => new string('-', x))));
            foreach (var row in rows) _t.AppendLine(RowLine(row, widths));
            _t.AppendLine();

            _m.AppendLine("| " + string.Join(" | ", headers.Select(MdCell)) + " |");
            _m.AppendLine("| " + string.Join(" | ", headers.Select(_ => "---")) + " |");
            foreach (var row in rows)
            {
                var cells = new List<string>();
                for (int c = 0; c < cols; c++) cells.Add(MdCell(c < row.Length ? row[c] : ""));
                _m.AppendLine("| " + string.Join(" | ", cells) + " |");
            }
            _m.AppendLine();
        }

        // Markdown-only structured table (used for descriptive-stats tables so TXT
        // can keep the engine's own plain-text rendering instead of duplicating).
        public void MarkdownOnlyTable(StatisticsOutputTable tbl)
        {
            if (!string.IsNullOrWhiteSpace(tbl.Title)) _m.AppendLine("**" + tbl.Title.Trim() + "**\n");
            if (tbl.Columns.Count == 0) return;
            _m.AppendLine("| " + string.Join(" | ", tbl.Columns.Select(MdCell)) + " |");
            _m.AppendLine("| " + string.Join(" | ", tbl.Columns.Select(_ => "---")) + " |");
            foreach (var row in tbl.Rows)
                _m.AppendLine("| " + string.Join(" | ", row.Cells.Select(MdCell)) + " |");
            _m.AppendLine();
        }

        // TXT-only preformatted block (paired with MarkdownOnlyTable).
        public void TextOnlyBlock(string caption, string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return;
            if (!string.IsNullOrWhiteSpace(caption)) _t.AppendLine(caption + ":");
            _t.AppendLine(body.TrimEnd());
            _t.AppendLine();
        }

        // Preformatted block in BOTH formats (raw aggregate stat output).
        public void CodeBlock(string caption, string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return;
            if (!string.IsNullOrWhiteSpace(caption))
            {
                _t.AppendLine(caption + ":");
                _m.AppendLine("*" + caption + "*");
                _m.AppendLine();
            }
            _t.AppendLine(body.TrimEnd());
            _t.AppendLine();
            _m.AppendLine("```");
            _m.AppendLine(body.TrimEnd());
            _m.AppendLine("```");
            _m.AppendLine();
        }

        private static string RowLine(string[] cells, int[] widths)
        {
            var parts = new List<string>();
            for (int c = 0; c < widths.Length; c++)
                parts.Add((c < cells.Length ? cells[c] : "").PadRight(widths[c]));
            return string.Join(" | ", parts).TrimEnd();
        }

        private static string MdCell(string s)
            => (s ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
    }
}
