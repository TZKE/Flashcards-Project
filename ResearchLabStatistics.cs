using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AIFlashcardMaker;

// ===========================================================================
// Research Lab — Phase 4A: deterministic descriptive statistics foundation.
//
// DESIGN RULES (audit-critical):
//   * Every number here is produced by deterministic C# code. There are NO AI
//     calls anywhere in this file and no AI involvement in any calculation.
//   * The Extraction Sheet is the source of truth for MEANING (type, level,
//     role, required, coding/value labels). The CSV supplies raw VALUES only.
//     CSV inference is used solely for data-quality checks, never to decide
//     how a variable is analyzed.
//   * No inferential statistics: no p-values, tests, correlations, regression,
//     confidence intervals, odds ratios, or survival analysis. Phase 4A is
//     descriptive only.
//   * No WPF dependencies — everything here is headless-testable.
//   * Full precision is kept internally; rounding happens only at display/
//     export time via the selected decimals.
// ===========================================================================

// ---------------------------------------------------------------------------
// Missing-value policy. Central so the engine, quality checks, and audit
// notes always agree on exactly what counts as missing.
// ---------------------------------------------------------------------------
public static class StatisticsMissingTokens
{
    // Case-insensitive tokens treated as missing everywhere in Phase 4A.
    public static readonly string[] Tokens =
        { "na", "n/a", "null", "missing", ".", "-", "unknown", "not available" };

    public static bool IsMissing(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        string s = value.Trim().ToLowerInvariant();
        return Tokens.Contains(s);
    }

    public static string AuditDescription =>
        "Missing-value tokens (case-insensitive): empty, whitespace, " +
        string.Join(", ", Tokens.Select(t => "\"" + t + "\"")) + ".";
}

// ---------------------------------------------------------------------------
// Raw dataset parsed from the stored CSV copy. Values are kept as raw strings;
// interpretation happens per-variable using the extraction sheet.
// ---------------------------------------------------------------------------
public sealed class StatisticsDataset
{
    public string FileName { get; set; } = "";
    public List<string> ColumnNames { get; set; } = new();
    public List<string[]> Rows { get; set; } = new();

    // Structural findings recorded at parse time (never throw — report).
    public int RaggedRowCount { get; set; }          // rows with a different cell count than the header
    public List<string> BlankHeaderColumns { get; set; } = new();
    public List<string> DuplicateColumnNames { get; set; } = new();

    public int RowCount => Rows.Count;
    public int ColumnCount => ColumnNames.Count;

    // Case-insensitive, trimmed header lookup. Returns -1 when absent.
    public int ColumnIndexOf(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;
        string key = name.Trim();
        for (int i = 0; i < ColumnNames.Count; i++)
            if (string.Equals(ColumnNames[i].Trim(), key, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    // Cell accessor that treats short (ragged) rows as missing instead of
    // throwing. Extra cells beyond the header width are ignored by design.
    public string Cell(int row, int col)
    {
        if (row < 0 || row >= Rows.Count || col < 0) return "";
        var r = Rows[row];
        return col < r.Length ? r[col] : "";
    }

    public List<string> ColumnValues(int col)
    {
        var list = new List<string>(Rows.Count);
        for (int r = 0; r < Rows.Count; r++) list.Add(Cell(r, col));
        return list;
    }
}

// ---------------------------------------------------------------------------
// CSV reader for the statistics engine. Same RFC-4180-ish dialect as the
// Phase 3 sample importer (quoted fields, embedded commas, doubled quotes;
// line-based). Never throws on malformed content — problems are recorded on
// the dataset so the readiness layer can explain them.
// ---------------------------------------------------------------------------
public static class StatisticsCsvReader
{
    public static bool TryReadFile(string path, out StatisticsDataset dataset, out string error)
    {
        dataset = new StatisticsDataset();
        error = "";
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                error = "The stored dataset file could not be found.";
                return false;
            }
            return TryReadText(File.ReadAllText(path), Path.GetFileName(path), out dataset, out error);
        }
        catch
        {
            error = "The stored dataset file could not be read.";
            return false;
        }
    }

    public static bool TryReadText(string text, string fileName, out StatisticsDataset dataset, out string error)
    {
        dataset = new StatisticsDataset { FileName = fileName ?? "" };
        error = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "The dataset file is empty.";
            return false;
        }

        string[]? headers = null;
        foreach (var rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (headers is null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;   // leading blank lines
                headers = ParseLine(line);
                continue;
            }
            if (string.IsNullOrWhiteSpace(line)) continue;       // skip fully blank lines
            var cells = ParseLine(line);
            if (cells.Length != headers.Length) dataset.RaggedRowCount++;
            dataset.Rows.Add(cells);
        }

        if (headers is null || headers.Length == 0)
        {
            error = "No column headers were found in the dataset file.";
            return false;
        }

        // Header hygiene: blank headers get stable placeholder names; duplicate
        // names (case-insensitive) are recorded for the quality checks.
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Length; i++)
        {
            string name = headers[i].Trim();
            if (name.Length == 0)
            {
                name = $"column_{i + 1}";
                dataset.BlankHeaderColumns.Add(name);
            }
            if (seen.TryGetValue(name, out int count))
            {
                seen[name] = count + 1;
                if (!dataset.DuplicateColumnNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    dataset.DuplicateColumnNames.Add(name);
            }
            else seen[name] = 1;
            dataset.ColumnNames.Add(name);
        }
        return true;
    }

    // Same splitting rules as MainWindow.ParseCsvLine so the stored copy is
    // read exactly like the profiling sample was.
    public static string[] ParseLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
        }
        fields.Add(sb.ToString());
        return fields.Select(f => f.Trim()).ToArray();
    }
}

// ---------------------------------------------------------------------------
// Variable ↔ CSV column matching, injected by the caller. The UI builds this
// from the Phase 3 matching engine (names, labels, aliases) so statistics and
// Data Extraction always agree on which column belongs to which variable.
// ---------------------------------------------------------------------------
public sealed class StatisticsMatchInput
{
    // variable Id -> matched CSV column name
    public Dictionary<string, string> VariableColumn { get; set; } = new();
    public List<string> MetadataColumns { get; set; } = new();   // Timestamp/Username/… (ignored by design)
    public List<string> CsvOnlyColumns { get; set; } = new();    // non-metadata columns with no variable
}

// ---------------------------------------------------------------------------
// One data-quality or readiness finding. Blockers stop analysis; warnings
// allow it but are surfaced for review; info is context only.
// ---------------------------------------------------------------------------
public enum StatisticsSeverity { Info = 0, Warning = 1, Blocker = 2 }

public sealed class DataQualityIssue
{
    public StatisticsSeverity Severity { get; set; } = StatisticsSeverity.Info;
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public string VariableName { get; set; } = "";
    public string ActionHint { get; set; } = "";

    [JsonIgnore]
    public string SeverityDisplay => Severity switch
    {
        StatisticsSeverity.Blocker => "Blocker",
        StatisticsSeverity.Warning => "Warning",
        _ => "Note"
    };
}

// ---------------------------------------------------------------------------
// Readiness dashboard model.
// ---------------------------------------------------------------------------
public enum StatisticsReadinessState { Blocked = 0, NeedsReview = 1, Ready = 2 }

// One small status card on the readiness dashboard.
// Kind: "Good" | "Warn" | "Bad" | "Muted" — drives the card accent color.
public sealed class StatisticsReadinessCard
{
    public string Title { get; set; } = "";
    public string Value { get; set; } = "";
    public string Kind { get; set; } = "Muted";
}

public sealed class StatisticsReadinessResult
{
    public StatisticsReadinessState State { get; set; } = StatisticsReadinessState.Blocked;
    public int Score { get; set; }
    public string Explanation { get; set; } = "";
    public List<DataQualityIssue> Issues { get; set; } = new();
    public List<StatisticsReadinessCard> Cards { get; set; } = new();

    // "UploadCsv" | "UploadCompleteCsv" | "GoToDataExtraction" |
    // "ResolveRequiredIssues" | "RunStats"
    public string PrimaryActionCode { get; set; } = "RunStats";
    public string PrimaryActionLabel { get; set; } = "Run Descriptive Statistics";

    public int MatchedVariableCount { get; set; }
    public int AnalyzableVariableCount { get; set; }
    public int TotalVariableCount { get; set; }

    [JsonIgnore] public bool CanRun => State != StatisticsReadinessState.Blocked;
    [JsonIgnore] public List<DataQualityIssue> Blockers => Issues.Where(i => i.Severity == StatisticsSeverity.Blocker).ToList();
    [JsonIgnore] public List<DataQualityIssue> Warnings => Issues.Where(i => i.Severity == StatisticsSeverity.Warning).ToList();

    [JsonIgnore]
    public string StateDisplay => State switch
    {
        StatisticsReadinessState.Ready => "Ready for descriptive statistics",
        StatisticsReadinessState.NeedsReview => "Ready with warnings",
        _ => "Blocked"
    };
}

// ---------------------------------------------------------------------------
// Per-variable prepared values — the single shared interpretation step used by
// BOTH the readiness checks and the engine, so they can never disagree.
// ---------------------------------------------------------------------------
public enum VariableAnalysisKind
{
    Frequencies,        // categorical / binary / ordinal / coded numeric
    Continuous,         // continuous / scale numeric
    TextSummary,        // free text and dates (valid/missing/unique only)
    Excluded            // identifier, unknown type, unmatched, or unusable
}

public sealed class VariablePreparation
{
    public ResearchVariable Variable { get; init; } = new();
    public VariableAnalysisKind Kind { get; set; } = VariableAnalysisKind.Excluded;
    public string ExclusionReason { get; set; } = "";
    public string MatchedColumn { get; set; } = "";
    public bool IsOrdinal { get; set; }

    public int TotalN { get; set; }
    public int MissingN { get; set; }                       // missing tokens (incl. short-row gaps)
    public List<string> MeaningfulValues { get; } = new();  // non-missing raw values, trimmed

    // Continuous-only: values that parsed as numbers, and meaningful values
    // that did not (kept separate from missing — they are INVALID, not absent).
    public List<double> NumericValues { get; } = new();
    public List<string> InvalidNumericValues { get; } = new();

    public Dictionary<string, string> ValueLabelMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int ValidN => Kind == VariableAnalysisKind.Continuous ? NumericValues.Count : MeaningfulValues.Count;
    public double MissingPercent => TotalN == 0 ? 0 : 100.0 * MissingN / TotalN;

    public List<string> DistinctMeaningful =>
        MeaningfulValues.GroupBy(v => v, StringComparer.OrdinalIgnoreCase).Select(g => g.Key).ToList();
}

// ---------------------------------------------------------------------------
// Persisted results. Numbers are stored at FULL precision; formatting is
// applied only when tables are rendered/exported. All members are simple
// JSON-friendly types so older project files load unchanged (a missing record
// deserializes to null).
// ---------------------------------------------------------------------------
public sealed class FrequencyRowResult
{
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";
    public int Count { get; set; }
}

public sealed class VariableDescriptiveResult
{
    public string VariableName { get; set; } = "";
    public string Label { get; set; } = "";
    public string TypeDisplay { get; set; } = "";
    public string Role { get; set; } = "";
    public string MatchedColumn { get; set; } = "";

    // "Frequencies" | "Continuous" | "TextSummary" | "Excluded"
    public string AnalysisKind { get; set; } = "Excluded";
    public string ExclusionReason { get; set; } = "";
    public bool IncludeCumulativePercent { get; set; }

    // False only for an ORDINAL frequency variable whose category order could not
    // be determined (no coding and not deterministically inferable). Cumulative
    // percent is then hidden and a note is shown. Always true otherwise.
    public bool OrderResolved { get; set; } = true;

    public int TotalN { get; set; }
    public int ValidN { get; set; }
    public int MissingN { get; set; }
    public int InvalidN { get; set; }

    // Continuous (null when not applicable; SD null when ValidN < 2).
    public double? Mean { get; set; }
    public double? SampleSd { get; set; }
    public double? Median { get; set; }
    public double? Q1 { get; set; }
    public double? Q3 { get; set; }
    public double? Iqr { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }

    // Frequencies.
    public List<FrequencyRowResult> Frequencies { get; set; } = new();
    public int OtherCategoryCount { get; set; }      // categories folded into "Other"
    public int OtherValueCount { get; set; }         // observations folded into "Other"

    // Text summary.
    public int UniqueCount { get; set; }
    public string MostFrequentValue { get; set; } = "";
    public int MostFrequentCount { get; set; }

    // "OK" | "Review" | "High missingness"
    public string QualityStatus { get; set; } = "OK";

    [JsonIgnore] public double MissingPercent => TotalN == 0 ? 0 : 100.0 * MissingN / TotalN;
}

public sealed class DescriptiveStatisticsRecord
{
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public string SourceFingerprint { get; set; } = "";

    public string ReadinessStateAtRun { get; set; } = "";
    public int ReadinessScoreAtRun { get; set; }

    public string DatasetFileName { get; set; } = "";
    public int RowsAnalyzed { get; set; }
    public int DatasetColumnCount { get; set; }
    public int? TargetSampleSize { get; set; }

    public int TotalVariables { get; set; }
    public int VariablesAnalyzed { get; set; }
    public int MatchedVariables { get; set; }

    public long TotalCells { get; set; }
    public long MissingCells { get; set; }
    public double OverallMissingPercent { get; set; }

    public List<string> MetadataColumnsIgnored { get; set; } = new();
    public List<string> CsvOnlyColumnsIgnored { get; set; } = new();

    public List<VariableDescriptiveResult> Variables { get; set; } = new();
    public List<string> ExcludedVariableNotes { get; set; } = new();
    public List<string> QualityNotes { get; set; } = new();
    public List<string> AuditNotes { get; set; } = new();

    // Output options captured at run time (display can re-render with these).
    public int Decimals { get; set; } = 2;
    public string OutputStyle { get; set; } = "Academic Clean";
    public bool IncludeMissingSummary { get; set; } = true;
    public bool IncludeAuditNotes { get; set; } = true;
    public bool IncludeIgnoredColumnsNote { get; set; } = true;
    public bool IncludeTextSummary { get; set; } = true;

    [JsonIgnore]
    public string GeneratedDisplay => GeneratedAt.ToLocalTime().ToString("MMM d, yyyy · h:mm tt", CultureInfo.InvariantCulture);
}

// ---------------------------------------------------------------------------
// Renderable output table — plain strings so the UI and every export format
// share one deterministic representation.
// ---------------------------------------------------------------------------
public sealed class StatisticsTableRow
{
    public List<string> Cells { get; set; } = new();
    public bool IsEmphasis { get; set; }     // e.g. Total rows
    public bool IsSubtle { get; set; }       // e.g. Missing rows
}

public sealed class StatisticsOutputTable
{
    public string Section { get; set; } = "";
    public string Title { get; set; } = "";
    public string Caption { get; set; } = "";
    public List<string> Columns { get; set; } = new();
    public List<bool> RightAlign { get; set; } = new();
    public List<StatisticsTableRow> Rows { get; set; } = new();
}

// ---------------------------------------------------------------------------
// Fingerprint of the analysis inputs (sheet meaning + raw data + target).
// Used to detect stale results — if the fingerprint changes after a run, the
// stored results no longer describe the current project.
// ---------------------------------------------------------------------------
public static class StatisticsFingerprint
{
    public static string Compute(IEnumerable<ResearchVariable>? variables, string? datasetFilePath, int? targetSampleSize)
    {
        var sb = new StringBuilder();
        foreach (var v in variables ?? Enumerable.Empty<ResearchVariable>())
        {
            sb.Append(v.VariableName).Append('|').Append(v.QuestionLabel).Append('|')
              .Append(v.VariableType).Append('|').Append(v.MeasurementLevel).Append('|')
              .Append(v.Role).Append('|').Append(v.IsRequired ? '1' : '0').Append('|')
              .Append(v.Coding).Append('|').Append(v.ValueLabels).Append('|')
              .Append(v.MissingValueRule).Append('|')
              .Append(string.Join(";", v.SourceColumnAliases ?? new List<string>()))
              .Append('\n');
        }
        sb.Append("##TARGET##").Append(targetSampleSize?.ToString(CultureInfo.InvariantCulture) ?? "none").Append('\n');
        sb.Append("##DATA##");
        try
        {
            if (!string.IsNullOrWhiteSpace(datasetFilePath) && File.Exists(datasetFilePath))
            {
                using var fs = File.OpenRead(datasetFilePath);
                sb.Append(Convert.ToHexString(SHA256.HashData(fs)));
            }
            else sb.Append("nodata");
        }
        catch { sb.Append("unreadable"); }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}

// ---------------------------------------------------------------------------
// Shared interpretation of one variable against the dataset. Used by both the
// readiness service and the engine.
// ---------------------------------------------------------------------------
public static class StatisticsVariablePreparer
{
    // Deterministic thresholds (documented in audit notes / quality messages).
    public const double InvalidNumericBlockShare = 0.10;   // >10% meaningful non-numeric → blocker
    public const int HighCardinalityThreshold = 20;        // distinct categories that trigger a review warning
    public const int FrequencyTableCap = 25;               // categories shown before folding into "Other"
    public const double VariableHighMissingPercent = 50;   // ≥ → "High missingness"
    public const double VariableReviewMissingPercent = 20; // ≥ → "Review"
    public const double OverallHighMissingPercent = 50;    // dataset-wide warning

    public static VariablePreparation Prepare(ResearchVariable v, StatisticsDataset? data, string? matchedColumn)
    {
        var prep = new VariablePreparation { Variable = v, MatchedColumn = (matchedColumn ?? "").Trim() };

        string type = (v.VariableType ?? "").Trim();
        string level = (v.MeasurementLevel ?? "").Trim();
        string role = (v.Role ?? "").Trim();

        // ---- Kind from the SHEET (source of truth), never from the CSV ----
        if (role.Equals("Identifier", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("ID", StringComparison.OrdinalIgnoreCase))
        {
            prep.Kind = VariableAnalysisKind.Excluded;
            prep.ExclusionReason = "Identifier variables are excluded from descriptive analysis.";
        }
        else if (type.Equals("Unknown", StringComparison.OrdinalIgnoreCase) || type.Length == 0)
        {
            prep.Kind = VariableAnalysisKind.Excluded;
            prep.ExclusionReason = "The variable type is not set in the extraction sheet.";
        }
        else if (type.Equals("Continuous", StringComparison.OrdinalIgnoreCase))
        {
            prep.Kind = VariableAnalysisKind.Continuous;
        }
        else if (type.Equals("Numeric", StringComparison.OrdinalIgnoreCase))
        {
            // Numeric codes are frequencies when the sheet marks the level as
            // Nominal/Ordinal; otherwise numeric means a measured quantity.
            bool coded = level.Equals("Nominal", StringComparison.OrdinalIgnoreCase)
                      || level.Equals("Ordinal", StringComparison.OrdinalIgnoreCase);
            prep.Kind = coded ? VariableAnalysisKind.Frequencies : VariableAnalysisKind.Continuous;
            prep.IsOrdinal = level.Equals("Ordinal", StringComparison.OrdinalIgnoreCase);
        }
        else if (type.Equals("Binary", StringComparison.OrdinalIgnoreCase) ||
                 type.Equals("Categorical", StringComparison.OrdinalIgnoreCase))
        {
            prep.Kind = VariableAnalysisKind.Frequencies;
        }
        else if (type.Equals("Ordinal", StringComparison.OrdinalIgnoreCase))
        {
            prep.Kind = VariableAnalysisKind.Frequencies;
            prep.IsOrdinal = true;
        }
        else // Text, Date, anything else → safe summary only
        {
            prep.Kind = VariableAnalysisKind.TextSummary;
        }

        prep.ValueLabelMap = ParseValueLabels(v.ValueLabels, v.Coding);

        // ---- Values from the matched column ----
        if (data is null || prep.MatchedColumn.Length == 0)
        {
            if (prep.Kind != VariableAnalysisKind.Excluded)
            {
                prep.Kind = VariableAnalysisKind.Excluded;
                prep.ExclusionReason = "No matching dataset column was found for this variable.";
            }
            return prep;
        }

        int col = data.ColumnIndexOf(prep.MatchedColumn);
        if (col < 0)
        {
            prep.Kind = VariableAnalysisKind.Excluded;
            prep.ExclusionReason = $"The matched column “{prep.MatchedColumn}” was not found in the dataset.";
            return prep;
        }

        prep.TotalN = data.RowCount;
        for (int r = 0; r < data.RowCount; r++)
        {
            string raw = data.Cell(r, col).Trim();
            if (StatisticsMissingTokens.IsMissing(raw)) { prep.MissingN++; continue; }
            prep.MeaningfulValues.Add(raw);
        }

        if (prep.Kind == VariableAnalysisKind.Continuous)
        {
            foreach (var val in prep.MeaningfulValues)
            {
                if (TryParseNumeric(val, out double d)) prep.NumericValues.Add(d);
                else prep.InvalidNumericValues.Add(val);
            }
        }
        return prep;
    }

    // Numeric parsing: invariant culture; a single comma with no dot is
    // accepted as a decimal comma ("2,5" → 2.5). Documented in audit notes.
    public static bool TryParseNumeric(string value, out double result)
    {
        result = 0;
        string s = (value ?? "").Trim();
        if (s.Length == 0) return false;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out result)) return true;
        if (s.Count(c => c == ',') == 1 && !s.Contains('.'))
            return double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        return false;
    }

    // Parses "0 = No, 1 = Yes" / "1: Mild; 2: Moderate" style coding into a
    // code → label map. ValueLabels wins over Coding when both define a code.
    public static Dictionary<string, string> ParseValueLabels(string? valueLabels, string? coding)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void ParseInto(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (var part in text.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq < 0) eq = part.IndexOf(':');
                if (eq <= 0 || eq >= part.Length - 1) continue;
                string code = part.Substring(0, eq).Trim();
                string label = part.Substring(eq + 1).Trim();
                if (code.Length > 0 && label.Length > 0) map[code] = label;
            }
        }
        ParseInto(coding);
        ParseInto(valueLabels);   // later wins
        return map;
    }

    // "OK" | "Review" | "High missingness" for the missing-values summary.
    public static string MissingStatus(double missingPercent) =>
        missingPercent >= VariableHighMissingPercent ? "High missingness"
        : missingPercent >= VariableReviewMissingPercent ? "Review"
        : "OK";
}

// ---------------------------------------------------------------------------
// Readiness evaluation — decides Blocked / Needs Review / Ready before any
// statistics run, with a deterministic 0–100 readiness score (UX only).
// ---------------------------------------------------------------------------
public static class StatisticsReadinessService
{
    public static StatisticsReadinessResult Evaluate(
        IReadOnlyList<ResearchVariable> variables,
        StatisticsDataset? data,
        StatisticsMatchInput? match,
        int? targetSampleSize,
        IReadOnlyList<string>? sheetValidationErrors = null)
    {
        var r = new StatisticsReadinessResult();
        var issues = r.Issues;
        variables ??= new List<ResearchVariable>();
        match ??= new StatisticsMatchInput();

        var namedVars = variables.Where(v => !string.IsNullOrWhiteSpace(v.VariableName)).ToList();
        r.TotalVariableCount = namedVars.Count;

        bool sheetPresent = namedVars.Count > 0;
        bool dataPresent = data is { RowCount: > 0, ColumnCount: > 0 };

        // ---- Structural blockers -------------------------------------------
        if (!sheetPresent)
            issues.Add(Blocker("NoSheet",
                "No extraction sheet has been built yet. Statistics need the sheet to know how each variable should be analyzed.",
                action: "GoToDataExtraction"));

        if (data is null)
            issues.Add(Blocker("NoDataset",
                "No dataset is available for analysis. Upload your CSV file in Data Extraction.",
                action: "UploadCsv"));
        else if (data.RowCount == 0)
            issues.Add(Blocker("EmptyDataset",
                "The dataset contains no data rows. Upload a CSV file that includes your collected data.",
                action: "UploadCsv"));

        foreach (var err in sheetValidationErrors ?? Array.Empty<string>())
            if (!err.StartsWith("Sample size is incomplete", StringComparison.OrdinalIgnoreCase))
                issues.Add(Blocker("SheetValidation", "Extraction sheet validation: " + err, action: "ResolveRequiredIssues"));

        // ---- Sample size ----------------------------------------------------
        string sampleValue, sampleKind;
        if (dataPresent && targetSampleSize is int target && target > 0)
        {
            if (data!.RowCount < target)
            {
                int remaining = target - data.RowCount;
                issues.Add(Blocker("SampleIncomplete",
                    $"Sample size is incomplete: the dataset contains {data.RowCount} of {target} planned participants ({remaining} more required). Upload a complete CSV file before running statistics.",
                    action: "UploadCompleteCsv"));
                sampleValue = $"Incomplete · {data.RowCount}/{target}";
                sampleKind = "Bad";
            }
            else { sampleValue = $"Complete · {data.RowCount}/{target}"; sampleKind = "Good"; }
        }
        else if (dataPresent)
        {
            issues.Add(Warn("NoTargetSampleSize",
                "No target sample size was found. Confirm your sample size before running statistics."));
            sampleValue = $"Unknown target · {data!.RowCount} rows";
            sampleKind = "Warn";
        }
        else { sampleValue = "No dataset"; sampleKind = "Bad"; }

        // ---- Dataset structure ----------------------------------------------
        if (data is not null)
        {
            if (data.DuplicateColumnNames.Count > 0)
                issues.Add(Warn("DuplicateColumns",
                    "The dataset has duplicate column names: " + Preview(data.DuplicateColumnNames) +
                    ". Only the first matching column is analyzed for each variable."));
            if (data.BlankHeaderColumns.Count > 0)
                issues.Add(Warn("BlankHeaders",
                    $"{data.BlankHeaderColumns.Count} dataset column(s) had a blank header and were auto-named ({Preview(data.BlankHeaderColumns)})."));
            if (data.RaggedRowCount > 0)
                issues.Add(Warn("RaggedRows",
                    $"{data.RaggedRowCount} row(s) had a different number of cells than the header row. Short rows are treated as missing in the affected columns; extra cells are ignored."));
        }

        // ---- Per-variable preparation & type-specific checks -----------------
        var preparations = new List<VariablePreparation>();
        int matchedCount = 0, analyzable = 0;
        int requiredTotal = 0, requiredMet = 0;
        bool anyOutcomeDefined = false, outcomeUsable = false;
        string firstMissingOutcome = "", firstMissingRequired = "";

        foreach (var v in namedVars)
        {
            match.VariableColumn.TryGetValue(v.Id, out string? colName);
            var prep = StatisticsVariablePreparer.Prepare(v, data, colName);
            preparations.Add(prep);

            string name = v.VariableName.Trim();
            bool matched = !string.IsNullOrWhiteSpace(colName) && data is not null && data.ColumnIndexOf(colName!) >= 0;
            if (matched) matchedCount++;

            bool isOutcome = (v.Role ?? "").Trim().Equals("Outcome", StringComparison.OrdinalIgnoreCase);
            bool isIdentifier = (v.Role ?? "").Trim().Equals("Identifier", StringComparison.OrdinalIgnoreCase)
                             || (v.VariableType ?? "").Trim().Equals("ID", StringComparison.OrdinalIgnoreCase);
            if (isOutcome) anyOutcomeDefined = true;
            if (v.IsRequired && !isIdentifier) requiredTotal++;

            // Per-variable coverage checks only make sense once a dataset exists;
            // without one, the single "no dataset" blocker already explains it.
            if (!dataPresent) continue;

            // Required / outcome coverage (identifiers don't participate).
            if (v.IsRequired && !isIdentifier)
            {
                bool usable = matched && prep.Kind != VariableAnalysisKind.Excluded && prep.ValidN > 0;
                if (usable) requiredMet++;
                else
                {
                    if (firstMissingRequired.Length == 0) firstMissingRequired = name;
                    string why = !matched ? "has no matching dataset column"
                        : prep.Kind == VariableAnalysisKind.Excluded ? "cannot be analyzed (" + prep.ExclusionReason.TrimEnd('.') + ")"
                        : "has no valid values in the dataset";
                    issues.Add(Blocker("RequiredUnmet",
                        $"Required variable “{name}” {why}. Resolve this before running statistics.",
                        name, "ResolveRequiredIssues"));
                }
            }

            if (isOutcome && !isIdentifier)
            {
                bool usable = matched && prep.Kind != VariableAnalysisKind.Excluded && prep.ValidN > 0;
                if (usable) outcomeUsable = true;
                else
                {
                    if (firstMissingOutcome.Length == 0) firstMissingOutcome = name;
                    string why = !matched ? "has no matching dataset column"
                        : prep.Kind == VariableAnalysisKind.Excluded ? "cannot be analyzed (" + prep.ExclusionReason.TrimEnd('.') + ")"
                        : "has no valid values in the dataset";
                    issues.Add(Blocker("OutcomeUnmet",
                        $"Outcome variable “{name}” {why}. The primary outcome must be available before analysis.",
                        name, "ResolveRequiredIssues"));
                }
            }

            if (!matched)
            {
                // Unmatched optional variable — analysis can proceed without it.
                // (Required/outcome cases already raised blockers above.)
                if (!v.IsRequired && !isOutcome && !isIdentifier)
                    issues.Add(Warn("OptionalUnmatched",
                        $"Optional variable “{name}” has no matching dataset column and will be excluded from analysis.", name));
                continue;
            }

            if (prep.Kind == VariableAnalysisKind.Excluded)
            {
                if (prep.ExclusionReason.Contains("type is not set", StringComparison.OrdinalIgnoreCase) && !v.IsRequired && !isOutcome)
                    issues.Add(Warn("UnknownType",
                        $"Variable “{name}” has no type set in the extraction sheet and will be excluded from analysis.", name));
                continue;
            }

            analyzable++;

            if (prep.Kind == VariableAnalysisKind.Continuous && prep.MeaningfulValues.Count > 0)
            {
                int invalid = prep.InvalidNumericValues.Count;
                if (invalid > 0)
                {
                    double share = (double)invalid / prep.MeaningfulValues.Count;
                    string examples = Preview(prep.InvalidNumericValues.Select(Sanitize).Distinct().ToList());
                    if (share > StatisticsVariablePreparer.InvalidNumericBlockShare)
                        issues.Add(Blocker("ContinuousNonNumeric",
                            $"Continuous variable “{name}” has {invalid} non-numeric value(s) ({share * 100:F0}% of entered values; e.g. {examples}). Clean the data or correct the variable type in the extraction sheet.",
                            name, "ResolveRequiredIssues"));
                    else
                        issues.Add(Warn("ContinuousNonNumericRare",
                            $"Continuous variable “{name}” has {invalid} non-numeric value(s) (e.g. {examples}). They are excluded from calculations and reported as invalid entries.", name));
                }
            }

            if (prep.Kind == VariableAnalysisKind.Frequencies && prep.MeaningfulValues.Count > 0)
            {
                var distinct = prep.DistinctMeaningful;
                bool isBinary = (v.VariableType ?? "").Trim().Equals("Binary", StringComparison.OrdinalIgnoreCase);

                if (isBinary && distinct.Count > 2)
                {
                    bool allLabeled = prep.ValueLabelMap.Count > 0 &&
                                      distinct.All(dv => prep.ValueLabelMap.ContainsKey(dv) ||
                                                         prep.ValueLabelMap.Values.Any(lbl => string.Equals(lbl, dv, StringComparison.OrdinalIgnoreCase)));
                    if (allLabeled)
                        issues.Add(Warn("BinaryManyCategoriesLabeled",
                            $"Binary variable “{name}” has {distinct.Count} categories, all covered by its value labels. Review whether the coding is correct.", name));
                    else
                        issues.Add(Blocker("BinaryTooManyCategories",
                            $"Binary variable “{name}” has {distinct.Count} categories ({Preview(distinct.Select(Sanitize).ToList())}) but no value labels covering them. Fix the data or define the coding in the extraction sheet.",
                            name, "ResolveRequiredIssues"));
                }

                if (!isBinary && prep.ValueLabelMap.Count > 0)
                {
                    var outside = distinct.Where(dv => !prep.ValueLabelMap.ContainsKey(dv) &&
                                                       !prep.ValueLabelMap.Values.Any(lbl => string.Equals(lbl, dv, StringComparison.OrdinalIgnoreCase)))
                                          .Select(Sanitize).ToList();
                    if (outside.Count > 0)
                        issues.Add(Warn("OutsideLabels",
                            $"Variable “{name}” contains {outside.Count} value(s) outside its defined labels (e.g. {Preview(outside)}). Review the coding before interpreting results.", name));
                }

                if (prep.IsOrdinal && prep.ValueLabelMap.Count == 0)
                {
                    if (v.IsRequired || isOutcome)
                        issues.Add(Blocker("OrdinalNoCoding",
                            $"Ordinal variable “{name}” is {(isOutcome ? "the outcome" : "required")} but has no coding/value labels, so its category order cannot be verified. Define the coding in the extraction sheet.",
                            name, "ResolveRequiredIssues"));
                    else
                        issues.Add(Warn("OrdinalNoCodingOptional",
                            $"Ordinal variable “{name}” has no coding/value labels; categories are ordered by their values. Define the coding to control the order.", name));
                }

                if (distinct.Count > StatisticsVariablePreparer.HighCardinalityThreshold)
                    issues.Add(Warn("HighCardinality",
                        $"Variable “{name}” has {distinct.Count} distinct categories. Its frequency table shows the {StatisticsVariablePreparer.FrequencyTableCap} most frequent; check whether it should be a text variable instead.", name));
            }

            if (prep.TotalN > 0 && prep.MissingPercent >= StatisticsVariablePreparer.VariableHighMissingPercent)
                issues.Add(Warn("HighMissingVariable",
                    $"Variable “{name}” is missing in {prep.MissingPercent:F0}% of rows. Interpret its results with caution.", name));

            if (prep.TotalN > 0 && prep.ValidN == 0 && prep.Kind != VariableAnalysisKind.Excluded)
            {
                analyzable--;   // nothing to analyze after all
                if (v.IsRequired || isOutcome)
                { /* already raised as a required/outcome blocker above */ }
                else
                    issues.Add(Warn("AllMissing",
                        $"Variable “{name}” has no valid values in the dataset and will be reported as not analyzable.", name));
            }
        }

        // ---- Dataset-wide quality -------------------------------------------
        if (dataPresent)
        {
            long totalCells = (long)data!.RowCount * data.ColumnCount;
            long missingCells = 0;
            for (int c = 0; c < data.ColumnCount; c++)
                for (int rr = 0; rr < data.RowCount; rr++)
                    if (StatisticsMissingTokens.IsMissing(data.Cell(rr, c))) missingCells++;

            if (totalCells > 0 && missingCells == totalCells)
                issues.Add(Blocker("AllEmpty", "Every cell in the dataset is empty or a missing token. There is no data to analyze.", action: "UploadCsv"));
            else if (totalCells > 0 && 100.0 * missingCells / totalCells >= StatisticsVariablePreparer.OverallHighMissingPercent)
                issues.Add(Warn("HighMissingOverall",
                    $"{100.0 * missingCells / totalCells:F0}% of all dataset cells are missing. Review data completeness before interpreting results."));
        }

        if (match.CsvOnlyColumns.Count > 0)
            issues.Add(Warn("CsvOnlyColumns",
                $"{match.CsvOnlyColumns.Count} dataset column(s) are not linked to any variable and will not be analyzed ({Preview(match.CsvOnlyColumns)})."));
        if (match.MetadataColumns.Count > 0)
            issues.Add(Info("MetadataIgnored",
                $"{match.MetadataColumns.Count} system/dataset column(s) are ignored by design: {string.Join(", ", match.MetadataColumns)}."));

        if (sheetPresent && dataPresent && analyzable == 0)
            issues.Add(Blocker("NoAnalyzableVariables",
                "No variables can be analyzed. Match your variables to dataset columns and set their types in the extraction sheet.",
                action: "ResolveRequiredIssues"));

        r.MatchedVariableCount = matchedCount;
        r.AnalyzableVariableCount = analyzable;

        // ---- State ------------------------------------------------------------
        bool hasBlockers = issues.Any(i => i.Severity == StatisticsSeverity.Blocker);
        bool hasWarnings = issues.Any(i => i.Severity == StatisticsSeverity.Warning);
        r.State = hasBlockers ? StatisticsReadinessState.Blocked
                : hasWarnings ? StatisticsReadinessState.NeedsReview
                : StatisticsReadinessState.Ready;

        // ---- Score (deterministic, UX only — documented in audit notes) -------
        int score = 0;
        score += sheetPresent ? 20 : 0;
        score += dataPresent ? 20 : 0;
        score += sampleKind switch { "Good" => 15, "Warn" => 10, _ => 0 };
        score += requiredTotal == 0 ? 15 : (int)Math.Round(15.0 * requiredMet / requiredTotal);
        score += outcomeUsable ? 10 : anyOutcomeDefined ? 0 : 6;
        int quality = 20 - 5 * issues.Count(i => i.Severity == StatisticsSeverity.Blocker)
                         - 2 * issues.Count(i => i.Severity == StatisticsSeverity.Warning);
        score += Math.Max(0, quality);
        r.Score = Math.Clamp(score, 0, 100);

        // ---- Primary action ----------------------------------------------------
        var firstBlocker = issues.FirstOrDefault(i => i.Severity == StatisticsSeverity.Blocker);
        if (firstBlocker is null)
        {
            r.PrimaryActionCode = "RunStats";
            r.PrimaryActionLabel = "Run Descriptive Statistics";
        }
        else
        {
            r.PrimaryActionCode = string.IsNullOrEmpty(firstBlocker.ActionHint) ? "GoToDataExtraction" : firstBlocker.ActionHint;
            r.PrimaryActionLabel = r.PrimaryActionCode switch
            {
                "UploadCsv" => "Upload CSV",
                "UploadCompleteCsv" => "Upload Complete CSV",
                "ResolveRequiredIssues" => "Resolve Required Issues",
                _ => "Go to Data Extraction"
            };
        }

        r.Explanation = r.State switch
        {
            StatisticsReadinessState.Ready =>
                "All readiness checks passed. Descriptive statistics can be generated from your extraction sheet and dataset.",
            StatisticsReadinessState.NeedsReview =>
                $"Analysis can run, but {issues.Count(i => i.Severity == StatisticsSeverity.Warning)} warning(s) should be reviewed. Warnings are recorded with the results.",
            _ => (firstBlocker?.Message ?? "Analysis is blocked.") + " Analysis is blocked until required issues are resolved."
        };

        // ---- Dashboard cards ----------------------------------------------------
        r.Cards.Add(new StatisticsReadinessCard { Title = "Extraction Sheet", Value = sheetPresent ? $"Complete · {namedVars.Count} variables" : "Missing", Kind = sheetPresent ? "Good" : "Bad" });
        r.Cards.Add(new StatisticsReadinessCard { Title = "Dataset", Value = dataPresent ? $"Loaded · {data!.RowCount} rows" : "Missing", Kind = dataPresent ? "Good" : "Bad" });
        r.Cards.Add(new StatisticsReadinessCard { Title = "Sample Size", Value = sampleValue, Kind = sampleKind });
        r.Cards.Add(new StatisticsReadinessCard
        {
            Title = "Variables Matched",
            Value = $"{matchedCount}/{namedVars.Count}",
            Kind = namedVars.Count == 0 ? "Bad" : matchedCount == namedVars.Count ? "Good" : matchedCount == 0 ? "Bad" : "Warn"
        });
        r.Cards.Add(new StatisticsReadinessCard
        {
            Title = "Required Variables",
            Value = requiredTotal == 0 ? "None marked"
                  : !dataPresent ? "Awaiting dataset"
                  : requiredMet == requiredTotal ? "Complete"
                  : $"Missing · {requiredTotal - requiredMet} of {requiredTotal}",
            Kind = requiredTotal == 0 ? "Muted" : !dataPresent ? "Muted" : requiredMet == requiredTotal ? "Good" : "Bad"
        });
        r.Cards.Add(new StatisticsReadinessCard
        {
            Title = "Outcome Variable",
            Value = outcomeUsable ? "Found"
                  : !anyOutcomeDefined ? "Not specified"
                  : !dataPresent ? "Awaiting dataset"
                  : "Missing data",
            Kind = outcomeUsable ? "Good" : !anyOutcomeDefined ? "Warn" : !dataPresent ? "Muted" : "Bad"
        });
        int qb = issues.Count(i => i.Severity == StatisticsSeverity.Blocker);
        int qw = issues.Count(i => i.Severity == StatisticsSeverity.Warning);
        r.Cards.Add(new StatisticsReadinessCard
        {
            Title = "Data Quality",
            Value = qb > 0 ? $"Blocked · {qb} blocker(s)" : qw > 0 ? $"{qw} warning(s)" : "Good",
            Kind = qb > 0 ? "Bad" : qw > 0 ? "Warn" : "Good"
        });

        return r;
    }

    private static DataQualityIssue Blocker(string code, string msg, string variable = "", string action = "")
        => new() { Severity = StatisticsSeverity.Blocker, Code = code, Message = msg, VariableName = variable, ActionHint = action };
    private static DataQualityIssue Warn(string code, string msg, string variable = "")
        => new() { Severity = StatisticsSeverity.Warning, Code = code, Message = msg, VariableName = variable };
    private static DataQualityIssue Info(string code, string msg)
        => new() { Severity = StatisticsSeverity.Info, Code = code, Message = msg };

    private static string Preview(List<string> items, int max = 3)
        => string.Join(", ", items.Take(max).Select(s => "“" + Sanitize(s) + "”")) + (items.Count > max ? $" and {items.Count - max} more" : "");

    private static string Sanitize(string s)
    {
        s = (s ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length <= 30 ? s : s.Substring(0, 30).TrimEnd() + "…";
    }
}

// ---------------------------------------------------------------------------
// The descriptive statistics engine. Deterministic; documented methods:
//   * Mean = sum / n (full double precision).
//   * Sample SD = sqrt( Σ(x−mean)² / (n−1) ); requires n ≥ 2, otherwise N/A.
//   * Median/Q1/Q3 use linear interpolation on the sorted values
//     (h = (n−1)·p; equivalent to Excel QUARTILE.INC / R type 7). IQR = Q3−Q1.
//   * Percent = count / total N · 100. Valid percent = count / valid N · 100.
// ---------------------------------------------------------------------------
public static class DescriptiveStatisticsEngine
{
    public static DescriptiveStatisticsRecord Run(
        IReadOnlyList<ResearchVariable> variables,
        StatisticsDataset data,
        StatisticsMatchInput match,
        int? targetSampleSize,
        StatisticsReadinessResult readiness,
        string sourceFingerprint)
    {
        variables ??= new List<ResearchVariable>();
        match ??= new StatisticsMatchInput();
        var record = new DescriptiveStatisticsRecord
        {
            GeneratedAt = DateTime.UtcNow,
            SourceFingerprint = sourceFingerprint ?? "",
            ReadinessStateAtRun = readiness?.StateDisplay ?? "",
            ReadinessScoreAtRun = readiness?.Score ?? 0,
            DatasetFileName = data.FileName,
            RowsAnalyzed = data.RowCount,
            DatasetColumnCount = data.ColumnCount,
            TargetSampleSize = targetSampleSize,
            MetadataColumnsIgnored = new List<string>(match.MetadataColumns),
            CsvOnlyColumnsIgnored = new List<string>(match.CsvOnlyColumns)
        };

        var namedVars = variables.Where(v => !string.IsNullOrWhiteSpace(v.VariableName)).ToList();
        record.TotalVariables = namedVars.Count;

        // ---- Dataset-wide missing cells ------------------------------------
        long totalCells = (long)data.RowCount * data.ColumnCount;
        long missingCells = 0;
        for (int c = 0; c < data.ColumnCount; c++)
            for (int r = 0; r < data.RowCount; r++)
                if (StatisticsMissingTokens.IsMissing(data.Cell(r, c))) missingCells++;
        record.TotalCells = totalCells;
        record.MissingCells = missingCells;
        record.OverallMissingPercent = totalCells == 0 ? 0 : 100.0 * missingCells / totalCells;

        // ---- Per-variable results -------------------------------------------
        foreach (var v in namedVars)
        {
            match.VariableColumn.TryGetValue(v.Id, out string? colName);
            var prep = StatisticsVariablePreparer.Prepare(v, data, colName);

            var result = new VariableDescriptiveResult
            {
                VariableName = v.VariableName.Trim(),
                Label = (v.QuestionLabel ?? "").Trim(),
                TypeDisplay = DescribeType(v),
                Role = (v.Role ?? "").Trim(),
                MatchedColumn = prep.MatchedColumn,
                TotalN = prep.TotalN,
                MissingN = prep.MissingN
            };

            if (prep.Kind == VariableAnalysisKind.Excluded)
            {
                result.AnalysisKind = "Excluded";
                result.ExclusionReason = prep.ExclusionReason;
                record.ExcludedVariableNotes.Add($"{result.VariableName} — {prep.ExclusionReason}");
                record.Variables.Add(result);
                continue;
            }

            if (prep.ValidN == 0)
            {
                // No valid values: never crash — report as not analyzable.
                result.AnalysisKind = "Excluded";
                result.ExclusionReason = "No valid values in the dataset (all rows missing or invalid).";
                result.ValidN = 0;
                result.InvalidN = prep.InvalidNumericValues.Count;
                result.QualityStatus = "High missingness";
                record.ExcludedVariableNotes.Add($"{result.VariableName} — not analyzable: no valid values.");
                record.Variables.Add(result);
                continue;
            }

            switch (prep.Kind)
            {
                case VariableAnalysisKind.Continuous:
                    ComputeContinuous(prep, result);
                    break;
                case VariableAnalysisKind.Frequencies:
                    ComputeFrequencies(prep, result);
                    break;
                default:
                    ComputeTextSummary(prep, result);
                    break;
            }

            result.QualityStatus = StatisticsVariablePreparer.MissingStatus(result.MissingPercent);
            record.Variables.Add(result);
        }

        record.MatchedVariables = record.Variables.Count(v => v.MatchedColumn.Length > 0);
        record.VariablesAnalyzed = record.Variables.Count(v => v.AnalysisKind != "Excluded");

        // ---- Quality notes: readiness findings frozen at run time ------------
        foreach (var i in readiness?.Issues ?? new List<DataQualityIssue>())
            record.QualityNotes.Add($"{i.SeverityDisplay}: {i.Message}");

        // Ordinal variables whose order could not be determined: cumulative
        // percent is hidden (never guessed) and the student is told why.
        foreach (var uv in record.Variables.Where(v => v.AnalysisKind == "Frequencies" && !v.OrderResolved))
            record.QualityNotes.Add($"Note: Cumulative percent is hidden for “{uv.VariableName}” because category order is not defined. Use Magic Fix or set value labels to define the order.");

        // ---- Audit notes ------------------------------------------------------
        record.AuditNotes.Add($"Analysis generated on {DateTime.Now:yyyy-MM-dd HH:mm} from deterministic calculations. No AI was involved in any numeric result.");
        record.AuditNotes.Add($"Rows analyzed: {record.RowsAnalyzed}. Dataset file: {record.DatasetFileName}. Variables in extraction sheet: {record.TotalVariables}; analyzed: {record.VariablesAnalyzed}.");
        record.AuditNotes.Add("Variable types, measurement levels, roles, and coding were taken from the Extraction Sheet (source of truth). The CSV supplied raw values only.");
        record.AuditNotes.Add(StatisticsMissingTokens.AuditDescription);
        record.AuditNotes.Add("Continuous variables are summarized with mean, sample standard deviation, median, Q1, Q3, IQR, minimum, and maximum. Sample SD uses n − 1 and requires at least 2 valid values (shown as N/A otherwise).");
        record.AuditNotes.Add("Quartiles and the median use linear interpolation on the sorted values (h = (n − 1) × p; equivalent to Excel QUARTILE.INC / R type 7). IQR = Q3 − Q1.");
        record.AuditNotes.Add("Frequency tables: Percent = count ÷ total N × 100; Valid percent = count ÷ valid N × 100 (missing values excluded). Cumulative percent is shown for ordinal variables only.");
        record.AuditNotes.Add("Numeric parsing uses invariant culture; a single decimal comma is accepted. Values that could not be parsed for a continuous variable are reported as invalid entries and excluded from calculations (they are not counted as missing).");
        record.AuditNotes.Add("Categories are grouped case-insensitively after trimming whitespace; the most frequent original spelling is displayed.");
        if (record.ExcludedVariableNotes.Count > 0)
            record.AuditNotes.Add("Excluded variables: " + string.Join(" · ", record.ExcludedVariableNotes));
        record.AuditNotes.Add("Full precision is kept internally; values are rounded only for display/export using the selected decimals.");
        record.AuditNotes.Add($"Readiness at run time: {record.ReadinessStateAtRun} (score {record.ReadinessScoreAtRun}/100 — a workflow indicator, not a scientific measure).");

        return record;
    }

    private static string DescribeType(ResearchVariable v)
    {
        string t = (v.VariableType ?? "").Trim();
        string l = (v.MeasurementLevel ?? "").Trim();
        return l.Length == 0 || l.Equals("NotApplicable", StringComparison.OrdinalIgnoreCase) ? t : $"{t} · {l}";
    }

    private static void ComputeContinuous(VariablePreparation prep, VariableDescriptiveResult r)
    {
        r.AnalysisKind = "Continuous";
        r.ValidN = prep.NumericValues.Count;
        r.InvalidN = prep.InvalidNumericValues.Count;

        var sorted = prep.NumericValues.OrderBy(x => x).ToList();
        int n = sorted.Count;
        r.Min = sorted[0];
        r.Max = sorted[n - 1];
        r.Mean = sorted.Sum() / n;
        r.Median = Quantile(sorted, 0.50);
        r.Q1 = Quantile(sorted, 0.25);
        r.Q3 = Quantile(sorted, 0.75);
        r.Iqr = r.Q3 - r.Q1;

        if (n >= 2)
        {
            double mean = r.Mean.Value;
            double ss = sorted.Sum(x => (x - mean) * (x - mean));
            r.SampleSd = Math.Sqrt(ss / (n - 1));
        }
        else r.SampleSd = null;   // SD requires at least 2 valid values
    }

    // Linear interpolation quantile (Excel QUARTILE.INC / R type 7).
    public static double Quantile(IReadOnlyList<double> sortedAscending, double p)
    {
        int n = sortedAscending.Count;
        if (n == 0) return double.NaN;
        if (n == 1) return sortedAscending[0];
        double h = (n - 1) * p;
        int lo = (int)Math.Floor(h);
        int hi = Math.Min(lo + 1, n - 1);
        double frac = h - lo;
        return sortedAscending[lo] + frac * (sortedAscending[hi] - sortedAscending[lo]);
    }

    private static void ComputeFrequencies(VariablePreparation prep, VariableDescriptiveResult r)
    {
        r.AnalysisKind = "Frequencies";
        r.ValidN = prep.MeaningfulValues.Count;

        // Case-insensitive grouping after trimming; display the most frequent
        // original spelling (ties broken alphabetically) so “Male”/“male” merge.
        var groups = prep.MeaningfulValues
            .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Display = g.GroupBy(x => x, StringComparer.Ordinal)
                           .OrderByDescending(x => x.Count())
                           .ThenBy(x => x.Key, StringComparer.Ordinal)
                           .First().Key,
                Count = g.Count()
            })
            .ToList();

        // Deterministic ordering, by priority:
        //   1. an unambiguous SAFE order inferred from the values themselves
        //      (numeric ranges / recognized ordinal scale) — ordinal only. This
        //      is checked FIRST and OVERRIDES stored coding, because a sheet's
        //      value-label order can be incomplete or simply wrong (a common
        //      real-world mistake — e.g. a scale relabeled after "Never" was
        //      added later, or coding entered in the wrong sequence). When the
        //      canonical order is knowable, it always wins for display; the
        //      stored label TEXT is still used per value, just re-sequenced.
        //   2. explicit value-label order (the student's coding) — used only
        //      when the canonical order above could not be determined,
        //   3. numeric codes ascending when every category is numeric,
        //   4. alphabetical fallback (order NOT considered known).
        // Cumulative percent is shown only for an ordinal variable whose order is
        // actually known (1–3); an alphabetical fallback hides it and warns.
        List<(string Display, int Count)> ordered;
        bool orderKnown;
        bool allNumeric = groups.All(g => StatisticsVariablePreparer.TryParseNumeric(g.Display, out _));
        List<string> canonicalOrder = new();
        bool canonicalOrdinal = prep.IsOrdinal
            && MagicFixOrdering.TryOrder(groups.Select(g => g.Display).ToList(), out canonicalOrder, out _, out _);

        if (canonicalOrdinal)
        {
            int Rank(string v) { int i = canonicalOrder.FindIndex(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase)); return i < 0 ? int.MaxValue : i; }
            ordered = groups.OrderBy(g => Rank(g.Display))
                            .Select(g => (g.Display, g.Count)).ToList();
            orderKnown = true;
        }
        else if (prep.ValueLabelMap.Count > 0)
        {
            var labelOrder = prep.ValueLabelMap.Keys.ToList();
            int OrderOf(string val)
            {
                for (int i = 0; i < labelOrder.Count; i++)
                    if (string.Equals(labelOrder[i], val, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(prep.ValueLabelMap[labelOrder[i]], val, StringComparison.OrdinalIgnoreCase))
                        return i;
                return int.MaxValue;   // unlabeled values go last, alphabetically
            }
            ordered = groups.OrderBy(g => OrderOf(g.Display))
                            .ThenBy(g => g.Display, StringComparer.OrdinalIgnoreCase)
                            .Select(g => (g.Display, g.Count)).ToList();
            orderKnown = true;
        }
        else if (allNumeric)
        {
            ordered = groups.OrderBy(g => { StatisticsVariablePreparer.TryParseNumeric(g.Display, out double d); return d; })
                            .Select(g => (g.Display, g.Count)).ToList();
            orderKnown = true;
        }
        else
        {
            ordered = groups.OrderBy(g => g.Display, StringComparer.OrdinalIgnoreCase)
                            .Select(g => (g.Display, g.Count)).ToList();
            orderKnown = !prep.IsOrdinal;   // nominal needs no order; ordinal-without-order is unknown
        }

        r.OrderResolved = orderKnown;
        r.IncludeCumulativePercent = prep.IsOrdinal && orderKnown;

        // High-cardinality: cap the table, fold the tail into "Other".
        if (ordered.Count > StatisticsVariablePreparer.FrequencyTableCap)
        {
            var kept = ordered.OrderByDescending(g => g.Count)
                              .ThenBy(g => g.Display, StringComparer.OrdinalIgnoreCase)
                              .Take(StatisticsVariablePreparer.FrequencyTableCap)
                              .Select(g => g.Display)
                              .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var folded = ordered.Where(g => !kept.Contains(g.Display)).ToList();
            r.OtherCategoryCount = folded.Count;
            r.OtherValueCount = folded.Sum(g => g.Count);
            ordered = ordered.Where(g => kept.Contains(g.Display)).ToList();
        }

        foreach (var (display, count) in ordered)
        {
            prep.ValueLabelMap.TryGetValue(display, out string? label);
            label ??= "";

            // Defensive: a numeric value must never display a DIFFERENT numeric
            // label (e.g. Value 2 → Label 3). That pattern only occurs when
            // stale/shifted coding maps positional codes onto raw numeric values
            // that don't start at 1 — never a legitimate value label. Blank it
            // rather than show a misleading number.
            if (label.Length > 0
                && StatisticsVariablePreparer.TryParseNumeric(display, out double dNum)
                && StatisticsVariablePreparer.TryParseNumeric(label, out double lNum)
                && Math.Abs(dNum - lNum) > 1e-9)
            {
                label = "";
            }

            r.Frequencies.Add(new FrequencyRowResult { Value = display, Label = label, Count = count });
        }
    }

    private static void ComputeTextSummary(VariablePreparation prep, VariableDescriptiveResult r)
    {
        r.AnalysisKind = "TextSummary";
        r.ValidN = prep.MeaningfulValues.Count;
        r.UniqueCount = prep.DistinctMeaningful.Count;

        var top = prep.MeaningfulValues
            .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (top is not null && top.Count() >= 2)
        {
            string s = top.Key.Replace('\n', ' ').Replace('\r', ' ').Trim();
            r.MostFrequentValue = s.Length <= 40 ? s : s.Substring(0, 40).TrimEnd() + "…";
            r.MostFrequentCount = top.Count();
        }
    }
}

// ---------------------------------------------------------------------------
// Renders a persisted record into display/export tables, applying the chosen
// decimals. Full precision stays in the record; this layer only formats.
// ---------------------------------------------------------------------------
public static class StatisticsTableBuilder
{
    public static List<StatisticsOutputTable> Build(DescriptiveStatisticsRecord rec)
    {
        var tables = new List<StatisticsOutputTable>();
        int d = Math.Clamp(rec.Decimals, 0, 3);
        string F(double? x) => x is null ? "N/A" : x.Value.ToString("F" + d, CultureInfo.InvariantCulture);
        string P(double x) => x.ToString("F" + Math.Max(1, d), CultureInfo.InvariantCulture);

        // 1 — Analysis Summary --------------------------------------------------
        var summary = new StatisticsOutputTable
        {
            Section = "Analysis Summary",
            Title = "Analysis Summary",
            Caption = "Generated from deterministic calculations.",
            Columns = new() { "", "" },
            RightAlign = new() { false, false }
        };
        summary.Rows.Add(Row("Dataset", rec.DatasetFileName.Length > 0 ? rec.DatasetFileName : "—"));
        summary.Rows.Add(Row("Rows analyzed", rec.RowsAnalyzed.ToString(CultureInfo.InvariantCulture)));
        summary.Rows.Add(Row("Variables analyzed", $"{rec.VariablesAnalyzed} of {rec.TotalVariables}"));
        if (rec.TargetSampleSize is int t && t > 0)
            summary.Rows.Add(Row("Target sample size", t.ToString(CultureInfo.InvariantCulture)));
        summary.Rows.Add(Row("Generated", rec.GeneratedDisplay));
        summary.Rows.Add(Row("Readiness at run time", $"{rec.ReadinessStateAtRun} (score {rec.ReadinessScoreAtRun}/100)"));
        tables.Add(summary);

        // 2 — Dataset Summary ---------------------------------------------------
        var ds = new StatisticsOutputTable
        {
            Section = "Dataset Summary",
            Title = "Dataset Summary",
            Columns = new() { "", "" },
            RightAlign = new() { false, false }
        };
        ds.Rows.Add(Row("Rows (participants)", rec.RowsAnalyzed.ToString(CultureInfo.InvariantCulture)));
        ds.Rows.Add(Row("Dataset columns", rec.DatasetColumnCount.ToString(CultureInfo.InvariantCulture)));
        ds.Rows.Add(Row("Matched variables", rec.MatchedVariables.ToString(CultureInfo.InvariantCulture)));
        ds.Rows.Add(Row("Total cells", rec.TotalCells.ToString(CultureInfo.InvariantCulture)));
        ds.Rows.Add(Row("Missing cells", $"{rec.MissingCells} ({P(rec.OverallMissingPercent)}%)"));
        if (rec.IncludeIgnoredColumnsNote)
        {
            if (rec.MetadataColumnsIgnored.Count > 0)
                ds.Rows.Add(Row("System columns ignored", string.Join(", ", rec.MetadataColumnsIgnored)));
            if (rec.CsvOnlyColumnsIgnored.Count > 0)
                ds.Rows.Add(Row("Unlinked columns (not analyzed)", string.Join(", ", rec.CsvOnlyColumnsIgnored)));
        }
        tables.Add(ds);

        // 3 — Case Processing Summary --------------------------------------------
        var cps = new StatisticsOutputTable
        {
            Section = "Case Processing Summary",
            Title = "Case Processing Summary",
            Caption = $"Total rows analyzed: {rec.RowsAnalyzed}.",
            Columns = new() { "Variable", "Included N", "Included %", "Missing N", "Missing %" },
            RightAlign = new() { false, true, true, true, true }
        };
        foreach (var v in rec.Variables.Where(v => v.AnalysisKind != "Excluded"))
        {
            double incPct = v.TotalN == 0 ? 0 : 100.0 * v.ValidN / v.TotalN;
            cps.Rows.Add(new StatisticsTableRow
            {
                Cells = new() { v.VariableName, v.ValidN.ToString(CultureInfo.InvariantCulture), P(incPct),
                                (v.MissingN + v.InvalidN).ToString(CultureInfo.InvariantCulture), P(100 - incPct) }
            });
        }
        tables.Add(cps);

        // 4 — Frequency tables (one per variable) ----------------------------------
        foreach (var v in rec.Variables.Where(v => v.AnalysisKind == "Frequencies"))
        {
            bool hasLabels = ShouldShowLabelColumn(v);
            var ft = new StatisticsOutputTable
            {
                Section = "Frequency Tables",
                Title = $"{DisplayName(v)} — Frequencies",
                Caption = BuildFrequencyCaption(v),
                Columns = v.IncludeCumulativePercent
                    ? (hasLabels ? new List<string> { "Value", "Value Label", "Frequency", "Percent", "Valid %", "Cumulative %" }
                                 : new List<string> { "Value", "Frequency", "Percent", "Valid %", "Cumulative %" })
                    : (hasLabels ? new List<string> { "Value", "Value Label", "Frequency", "Percent", "Valid %" }
                                 : new List<string> { "Value", "Frequency", "Percent", "Valid %" })
            };
            ft.RightAlign = ft.Columns.Select(c => c is not ("Value" or "Label")).ToList();

            double cum = 0;
            foreach (var f in v.Frequencies)
            {
                double pct = v.TotalN == 0 ? 0 : 100.0 * f.Count / v.TotalN;
                double vpct = v.ValidN == 0 ? 0 : 100.0 * f.Count / v.ValidN;
                cum += vpct;
                var cells = new List<string> { f.Value };
                if (hasLabels) cells.Add(f.Label);
                cells.Add(f.Count.ToString(CultureInfo.InvariantCulture));
                cells.Add(P(pct));
                cells.Add(P(vpct));
                if (v.IncludeCumulativePercent) cells.Add(P(Math.Min(cum, 100)));
                ft.Rows.Add(new StatisticsTableRow { Cells = cells });
            }
            if (v.OtherCategoryCount > 0)
            {
                double pct = v.TotalN == 0 ? 0 : 100.0 * v.OtherValueCount / v.TotalN;
                double vpct = v.ValidN == 0 ? 0 : 100.0 * v.OtherValueCount / v.ValidN;
                var cells = new List<string> { $"Other ({v.OtherCategoryCount} categories)" };
                if (hasLabels) cells.Add("");
                cells.Add(v.OtherValueCount.ToString(CultureInfo.InvariantCulture));
                cells.Add(P(pct)); cells.Add(P(vpct));
                if (v.IncludeCumulativePercent) cells.Add(P(100));
                ft.Rows.Add(new StatisticsTableRow { Cells = cells, IsSubtle = true });
            }
            if (v.MissingN > 0)
            {
                double pct = v.TotalN == 0 ? 0 : 100.0 * v.MissingN / v.TotalN;
                var cells = new List<string> { "Missing" };
                if (hasLabels) cells.Add("");
                cells.Add(v.MissingN.ToString(CultureInfo.InvariantCulture));
                cells.Add(P(pct)); cells.Add("—");
                if (v.IncludeCumulativePercent) cells.Add("—");
                ft.Rows.Add(new StatisticsTableRow { Cells = cells, IsSubtle = true });
            }
            var totalCells2 = new List<string> { "Total" };
            if (hasLabels) totalCells2.Add("");
            totalCells2.Add(v.TotalN.ToString(CultureInfo.InvariantCulture));
            totalCells2.Add(P(100)); totalCells2.Add(v.ValidN > 0 ? P(100) : "—");
            if (v.IncludeCumulativePercent) totalCells2.Add("");
            ft.Rows.Add(new StatisticsTableRow { Cells = totalCells2, IsEmphasis = true });

            tables.Add(ft);
        }

        // 5 — Descriptive statistics (combined continuous table) --------------------
        var cont = rec.Variables.Where(v => v.AnalysisKind == "Continuous").ToList();
        if (cont.Count > 0)
        {
            var dt = new StatisticsOutputTable
            {
                Section = "Descriptive Statistics",
                Title = "Descriptive Statistics — Continuous Variables",
                Caption = "Mean, sample SD (n − 1), median, quartiles (linear interpolation), and range.",
                Columns = new() { "Variable", "Valid N", "Missing", "Mean", "SD", "Median", "Q1", "Q3", "IQR", "Min", "Max" },
                RightAlign = new() { false, true, true, true, true, true, true, true, true, true, true }
            };
            foreach (var v in cont)
            {
                int missingShown = v.MissingN + v.InvalidN;
                dt.Rows.Add(new StatisticsTableRow
                {
                    Cells = new()
                    {
                        DisplayName(v),
                        v.ValidN.ToString(CultureInfo.InvariantCulture),
                        missingShown.ToString(CultureInfo.InvariantCulture) + (v.InvalidN > 0 ? $" ({v.InvalidN} invalid)" : ""),
                        F(v.Mean), F(v.SampleSd), F(v.Median), F(v.Q1), F(v.Q3), F(v.Iqr), F(v.Min), F(v.Max)
                    }
                });
            }
            tables.Add(dt);
        }

        // 6 — Text variable summary ---------------------------------------------
        var texts = rec.Variables.Where(v => v.AnalysisKind == "TextSummary").ToList();
        if (rec.IncludeTextSummary && texts.Count > 0)
        {
            var tt = new StatisticsOutputTable
            {
                Section = "Text Variable Summary",
                Title = "Text Variable Summary",
                Caption = "Free-text and date variables report counts only; raw responses are not listed.",
                Columns = new() { "Variable", "Valid N", "Missing N", "Unique values", "Most frequent (if repeated)" },
                RightAlign = new() { false, true, true, true, false }
            };
            foreach (var v in texts)
                tt.Rows.Add(new StatisticsTableRow
                {
                    Cells = new()
                    {
                        DisplayName(v),
                        v.ValidN.ToString(CultureInfo.InvariantCulture),
                        v.MissingN.ToString(CultureInfo.InvariantCulture),
                        v.UniqueCount.ToString(CultureInfo.InvariantCulture),
                        v.MostFrequentValue.Length > 0 ? $"{v.MostFrequentValue} (n={v.MostFrequentCount})" : "—"
                    }
                });
            tables.Add(tt);
        }

        // 7 — Missing values summary ----------------------------------------------
        if (rec.IncludeMissingSummary)
        {
            var mt = new StatisticsOutputTable
            {
                Section = "Missing Values Summary",
                Title = "Missing Values Summary",
                Caption = $"Status: OK < {StatisticsVariablePreparer.VariableReviewMissingPercent}% · Review ≥ {StatisticsVariablePreparer.VariableReviewMissingPercent}% · High missingness ≥ {StatisticsVariablePreparer.VariableHighMissingPercent}%.",
                Columns = new() { "Variable", "Label", "Valid N", "Missing N", "Missing %", "Status" },
                RightAlign = new() { false, false, true, true, true, false }
            };
            foreach (var v in rec.Variables.Where(v => v.TotalN > 0))
                mt.Rows.Add(new StatisticsTableRow
                {
                    Cells = new()
                    {
                        v.VariableName, v.Label, v.ValidN.ToString(CultureInfo.InvariantCulture),
                        v.MissingN.ToString(CultureInfo.InvariantCulture), P(v.MissingPercent),
                        StatisticsVariablePreparer.MissingStatus(v.MissingPercent)
                    }
                });
            tables.Add(mt);
        }

        // 8 — Data quality notes ------------------------------------------------
        if (rec.QualityNotes.Count > 0)
        {
            var qn = new StatisticsOutputTable
            {
                Section = "Data Quality Notes",
                Title = "Data Quality Notes",
                Caption = "Warnings and notes recorded at the time of analysis.",
                Columns = new() { "Note" },
                RightAlign = new() { false }
            };
            foreach (var q in rec.QualityNotes)
                qn.Rows.Add(new StatisticsTableRow { Cells = new() { q } });
            tables.Add(qn);
        }

        // 9 — Audit notes ---------------------------------------------------------
        if (rec.IncludeAuditNotes)
        {
            var an = new StatisticsOutputTable
            {
                Section = "Audit Notes",
                Title = "Audit Notes",
                Caption = "How these results were calculated.",
                Columns = new() { "Note" },
                RightAlign = new() { false }
            };
            foreach (var a in rec.AuditNotes)
                an.Rows.Add(new StatisticsTableRow { Cells = new() { a } });
            tables.Add(an);
        }

        return tables;

        static StatisticsTableRow Row(string k, string v) => new() { Cells = new() { k, v } };
    }

    // Priority: (1) a distinct extraction-sheet label goes in parentheses next
    // to the real variable name — the name is never hidden, just annotated;
    // (2) an already-clean variable name (no underscores) needs nothing further;
    // (3) an "ugly" auto-sanitized name (e.g. a question turned into an
    // identifier, full of underscores) is prettified for display ONLY — the
    // stored VariableName is never modified, so matching/coding is unaffected.
    private static string DisplayName(VariableDescriptiveResult v)
    {
        bool hasUsefulLabel = v.Label.Length > 0 && !string.Equals(v.Label, v.VariableName, StringComparison.OrdinalIgnoreCase);
        if (hasUsefulLabel) return $"{v.VariableName} ({Shorten(v.Label, 60)})";
        return v.VariableName.Contains('_') ? Shorten(PrettifyIdentifier(v.VariableName), 70) : v.VariableName;
    }

    private static readonly string[] QuestionStarters =
    {
        "do ", "does ", "did ", "is ", "are ", "was ", "were ", "has ", "have ", "had ",
        "can ", "could ", "will ", "would ", "should "
    };

    // Turns a sanitized identifier back into a readable phrase: underscores to
    // spaces, sentence case, and a trailing "?" when it reads like a yes/no
    // question (the common shape of a variable name auto-derived from a
    // survey question) and doesn't already end with punctuation.
    private static string PrettifyIdentifier(string name)
    {
        string s = (name ?? "").Trim().Replace('_', ' ');
        s = Regex.Replace(s, @"\s+", " ").Trim();
        if (s.Length == 0) return s;
        s = char.ToUpperInvariant(s[0]) + s.Substring(1);
        string lower = s.ToLowerInvariant();
        if (QuestionStarters.Any(q => lower.StartsWith(q)) && !s.EndsWith("?") && !s.EndsWith("."))
            s += "?";
        return s;
    }

    // A frequency table's Label/Value-Label column is shown only when it is
    // complete AND adds real information beyond the raw Value (Phase 4A output
    // polish). Never partial (some rows labeled, some not), never a label that
    // just repeats its own value, and never a bare numeric "code" attached to a
    // value that is already human-readable text (almost always a reversed
    // "label = code" mistake in the sheet's coding, not genuine information).
    private static bool ShouldShowLabelColumn(VariableDescriptiveResult v)
    {
        if (v.Frequencies.Count == 0) return false;
        if (v.Frequencies.Any(f => f.Label.Length == 0)) return false;   // incomplete
        if (v.Frequencies.All(f => string.Equals(f.Label.Trim(), f.Value.Trim(), StringComparison.OrdinalIgnoreCase)))
            return false;   // identical — adds nothing

        bool valuesAreText = v.Frequencies.All(f => !StatisticsVariablePreparer.TryParseNumeric(f.Value, out _));
        bool labelsAreNumeric = v.Frequencies.All(f => StatisticsVariablePreparer.TryParseNumeric(f.Label, out _));
        if (valuesAreText && labelsAreNumeric) return false;   // backwards/uninformative

        return true;
    }

    private static string BuildFrequencyCaption(VariableDescriptiveResult v)
    {
        var parts = new List<string> { $"{v.TypeDisplay}." };
        parts.Add($"Valid N = {v.ValidN}, missing = {v.MissingN}.");
        if (v.IncludeCumulativePercent) parts.Add("Cumulative percent follows the category order.");
        else if (!v.OrderResolved) parts.Add("Cumulative percent is hidden because category order is not defined.");
        if (v.OtherCategoryCount > 0) parts.Add($"The {v.OtherCategoryCount} least frequent categories are grouped as “Other”.");
        return string.Join(" ", parts);
    }

    private static string Shorten(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max).TrimEnd() + "…";
}

// ---------------------------------------------------------------------------
// Export: plain text, CSV, audit TXT, and a self-contained HTML report.
// Pure string builders — file dialogs and clipboard stay in the UI layer.
// ---------------------------------------------------------------------------
public static class StatisticsExportService
{
    public static string BuildPlainText(DescriptiveStatisticsRecord rec, List<StatisticsOutputTable> tables)
    {
        var sb = new StringBuilder();
        sb.AppendLine("DESCRIPTIVE STATISTICS REPORT");
        sb.AppendLine($"Generated: {rec.GeneratedDisplay}   Dataset: {rec.DatasetFileName}   Rows: {rec.RowsAnalyzed}");
        sb.AppendLine(new string('=', 78));
        string lastSection = "";
        foreach (var t in tables)
        {
            if (t.Section != lastSection)
            {
                sb.AppendLine();
                sb.AppendLine(t.Section.ToUpperInvariant());
                sb.AppendLine(new string('-', t.Section.Length));
                lastSection = t.Section;
            }
            if (!string.Equals(t.Title, t.Section, StringComparison.Ordinal))
                sb.AppendLine().AppendLine(t.Title);
            if (t.Caption.Length > 0) sb.AppendLine("  " + t.Caption);
            AppendPaddedTable(sb, t);
        }
        return sb.ToString();
    }

    public static string BuildTablePlainText(StatisticsOutputTable t)
    {
        var sb = new StringBuilder();
        sb.AppendLine(t.Title);
        if (t.Caption.Length > 0) sb.AppendLine(t.Caption);
        AppendPaddedTable(sb, t);
        return sb.ToString();
    }

    private static void AppendPaddedTable(StringBuilder sb, StatisticsOutputTable t)
    {
        const int MaxColWidth = 42;
        bool hasHeader = t.Columns.Any(c => c.Length > 0);
        var all = new List<List<string>>();
        if (hasHeader) all.Add(t.Columns.Select(Cap).ToList());
        foreach (var r in t.Rows) all.Add(r.Cells.Select(Cap).ToList());
        int cols = all.Max(r => r.Count);
        var widths = new int[cols];
        foreach (var r in all)
            for (int i = 0; i < r.Count; i++)
                widths[i] = Math.Max(widths[i], r[i].Length);

        void Line(List<string> cells)
        {
            var parts = new List<string>();
            for (int i = 0; i < cols; i++)
            {
                string cell = i < cells.Count ? cells[i] : "";
                bool right = i < t.RightAlign.Count && t.RightAlign[i];
                parts.Add(right ? cell.PadLeft(widths[i]) : cell.PadRight(widths[i]));
            }
            sb.AppendLine("  " + string.Join("  ", parts).TrimEnd());
        }

        if (hasHeader)
        {
            Line(t.Columns.Select(Cap).ToList());
            sb.AppendLine("  " + string.Join("  ", widths.Select(w => new string('-', w))));
        }
        foreach (var r in t.Rows) Line(r.Cells.Select(Cap).ToList());

        static string Cap(string s)
        {
            s = (s ?? "").Replace('\n', ' ').Replace('\r', ' ');
            return s.Length <= MaxColWidth ? s : s.Substring(0, MaxColWidth - 1).TrimEnd() + "…";
        }
    }

    public static string BuildCsv(DescriptiveStatisticsRecord rec, List<StatisticsOutputTable> tables)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Quote("Descriptive Statistics Report"));
        sb.AppendLine(Quote("Generated") + "," + Quote(rec.GeneratedDisplay));
        sb.AppendLine(Quote("Dataset") + "," + Quote(rec.DatasetFileName));
        sb.AppendLine(Quote("Rows analyzed") + "," + rec.RowsAnalyzed);
        foreach (var t in tables)
        {
            sb.AppendLine();
            sb.AppendLine(Quote(t.Section + " — " + t.Title));
            if (t.Caption.Length > 0) sb.AppendLine(Quote(t.Caption));
            if (t.Columns.Any(c => c.Length > 0))
                sb.AppendLine(string.Join(",", t.Columns.Select(Quote)));
            foreach (var r in t.Rows)
                sb.AppendLine(string.Join(",", r.Cells.Select(Quote)));
        }
        return sb.ToString();

        static string Quote(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
    }

    public static string BuildAuditNotesText(DescriptiveStatisticsRecord rec)
    {
        var sb = new StringBuilder();
        sb.AppendLine("AUDIT NOTES — DESCRIPTIVE STATISTICS");
        sb.AppendLine($"Generated: {rec.GeneratedDisplay}");
        sb.AppendLine($"Dataset: {rec.DatasetFileName} ({rec.RowsAnalyzed} rows)");
        sb.AppendLine(new string('-', 60));
        foreach (var a in rec.AuditNotes) sb.AppendLine("• " + a);
        if (rec.QualityNotes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("DATA QUALITY NOTES AT ANALYSIS TIME");
            sb.AppendLine(new string('-', 60));
            foreach (var q in rec.QualityNotes) sb.AppendLine("• " + q);
        }
        return sb.ToString();
    }

    public static string BuildHtml(DescriptiveStatisticsRecord rec, List<StatisticsOutputTable> tables)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset=\"utf-8\"/>");
        sb.AppendLine("<title>Descriptive Statistics Report</title><style>");
        sb.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;color:#22222e;margin:40px auto;max-width:900px;line-height:1.5;}");
        sb.AppendLine("h1{font-size:22px;border-bottom:2px solid #22222e;padding-bottom:8px;}");
        sb.AppendLine("h2{font-size:16px;margin-top:34px;color:#33334a;}");
        sb.AppendLine("h3{font-size:14px;margin:20px 0 4px 0;}");
        sb.AppendLine(".caption{font-size:12px;color:#666;margin:0 0 6px 0;}");
        sb.AppendLine("table{border-collapse:collapse;width:100%;font-size:13px;margin-bottom:10px;}");
        sb.AppendLine("th{border-top:2px solid #22222e;border-bottom:1px solid #22222e;text-align:left;padding:6px 10px;}");
        sb.AppendLine("td{padding:5px 10px;border-bottom:1px solid #e3e3ec;}");
        sb.AppendLine("tr.total td{border-top:1px solid #22222e;border-bottom:2px solid #22222e;font-weight:600;}");
        sb.AppendLine("tr.subtle td{color:#777;}");
        sb.AppendLine(".right{text-align:right;}");
        sb.AppendLine(".meta{font-size:12.5px;color:#555;}");
        sb.AppendLine("</style></head><body>");
        sb.AppendLine("<h1>Descriptive Statistics Report</h1>");
        sb.AppendLine($"<p class=\"meta\">Generated {H(rec.GeneratedDisplay)} · Dataset {H(rec.DatasetFileName)} · {rec.RowsAnalyzed} rows analyzed · {rec.VariablesAnalyzed} of {rec.TotalVariables} variables analyzed.<br/>Generated from deterministic calculations.</p>");

        string lastSection = "";
        foreach (var t in tables)
        {
            if (t.Section != lastSection) { sb.AppendLine($"<h2>{H(t.Section)}</h2>"); lastSection = t.Section; }
            if (!string.Equals(t.Title, t.Section, StringComparison.Ordinal)) sb.AppendLine($"<h3>{H(t.Title)}</h3>");
            if (t.Caption.Length > 0) sb.AppendLine($"<p class=\"caption\">{H(t.Caption)}</p>");
            sb.AppendLine("<table>");
            if (t.Columns.Any(c => c.Length > 0))
            {
                sb.Append("<tr>");
                for (int i = 0; i < t.Columns.Count; i++)
                    sb.Append($"<th{(Right(t, i) ? " class=\"right\"" : "")}>{H(t.Columns[i])}</th>");
                sb.AppendLine("</tr>");
            }
            foreach (var r in t.Rows)
            {
                string cls = r.IsEmphasis ? " class=\"total\"" : r.IsSubtle ? " class=\"subtle\"" : "";
                sb.Append($"<tr{cls}>");
                for (int i = 0; i < r.Cells.Count; i++)
                    sb.Append($"<td{(Right(t, i) ? " class=\"right\"" : "")}>{H(r.Cells[i])}</td>");
                sb.AppendLine("</tr>");
            }
            sb.AppendLine("</table>");
        }
        sb.AppendLine("</body></html>");
        return sb.ToString();

        static bool Right(StatisticsOutputTable t, int i) => i < t.RightAlign.Count && t.RightAlign[i];
        static string H(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}

// ===========================================================================
// Magic Fix — deterministic, LOCAL repair suggestions for the Extraction Sheet
// when its metadata does not match the actual dataset values. It never calls
// AI, never calculates statistics, never changes CSV values, and never applies
// anything on its own — it only proposes metadata repairs the user reviews and
// confirms. Everything here is headless-testable (no WPF dependency).
// ===========================================================================

// One proposed sheet repair. Editable fields (accepted / proposed type /
// proposed coding) raise change notifications so the review UI can bind them.
public sealed class MagicFixProposal : INotifyPropertyChanged
{
    public string VariableId { get; set; } = "";
    public string VariableName { get; set; } = "";
    public string Label { get; set; } = "";

    public string CurrentType { get; set; } = "";
    public string CurrentLevel { get; set; } = "";
    public string ObservedPreview { get; set; } = "";
    public string Explanation { get; set; } = "";

    // "High" | "Medium" | "Low"
    public string Confidence { get; set; } = "Low";
    // "safe" | "review" | "manual"
    public string Group { get; set; } = "manual";

    // Whether the repair actually changes the sheet. Manual/informational items
    // (order cannot be inferred, etc.) are shown for transparency but cannot be
    // applied.
    public bool IsApplicable { get; set; } = true;

    // When true, apply writes ProposedCoding into the variable's Coding.
    public bool SetsCoding { get; set; }

    private string _proposedType = "";
    public string ProposedType
    {
        get => _proposedType;
        set { if (_proposedType != value) { _proposedType = value; Raise(nameof(ProposedType)); Raise(nameof(ProposedTypeDisplay)); } }
    }

    private string _proposedCoding = "";
    public string ProposedCoding
    {
        get => _proposedCoding;
        set { if (_proposedCoding != value) { _proposedCoding = value; Raise(nameof(ProposedCoding)); } }
    }

    private bool _accepted;
    public bool Accepted
    {
        get => _accepted;
        set { if (_accepted != value) { _accepted = value; Raise(nameof(Accepted)); } }
    }

    // Measurement level implied by the (possibly edited) proposed type. Applied
    // alongside the type so the sheet stays internally consistent.
    [JsonIgnore]
    public string ProposedLevel => (ProposedType ?? "").Trim() switch
    {
        "Ordinal" => "Ordinal",
        "Categorical" or "Binary" => "Nominal",
        "Continuous" or "Numeric" => "Scale",
        "Text" or "Date" or "ID" => "NotApplicable",
        _ => string.IsNullOrWhiteSpace(CurrentLevel) ? "NotApplicable" : CurrentLevel
    };

    [JsonIgnore] public string CurrentTypeDisplay => Combine(CurrentType, CurrentLevel);
    [JsonIgnore] public string ProposedTypeDisplay => Combine(ProposedType, ProposedLevel);
    [JsonIgnore] public bool ChangesCoding => SetsCoding && !string.IsNullOrWhiteSpace(ProposedCoding);
    [JsonIgnore] public bool DefaultSelected => IsApplicable && Confidence == "High";

    [JsonIgnore]
    public int GroupOrder => Group switch { "safe" => 0, "review" => 1, _ => 2 };
    [JsonIgnore]
    public string GroupDisplay => Group switch
    {
        "safe" => "Safe repairs",
        "review" => "Needs your review",
        _ => "Not safe to fix automatically"
    };
    [JsonIgnore]
    public string ConfidenceDisplay => Confidence switch { "High" => "High", "Medium" => "Medium", _ => "Low" };

    private static string Combine(string type, string level)
    {
        type = (type ?? "").Trim();
        level = (level ?? "").Trim();
        return level.Length == 0 || level.Equals("NotApplicable", StringComparison.OrdinalIgnoreCase) ? type : $"{type} · {level}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

// The deterministic value-ordering engine used to decide whether a set of
// category strings has an OBVIOUS order (so a variable can safely become
// Ordinal). Two strategies: numeric-range extraction (with unit normalization
// and less/more-than qualifiers) and a fixed ordinal-word lexicon. If neither
// yields a strictly increasing, unambiguous ordering, the values are treated as
// unordered (→ a Categorical/Nominal repair instead).
public static class MagicFixOrdering
{
    // Ordinal words → rank for common research scales. Ranks are chosen so each
    // named scale is strictly increasing; near-synonyms may share a rank, which
    // correctly flags ambiguity if one variable mixes them. Multi-word phrases
    // are matched exactly before single-word fallbacks.
    private static readonly (string Phrase, double Rank)[] Lexicon =
    {
        // Frequency: Never < Rarely < Occasionally < Sometimes < Often < Frequently < Almost always < Always
        ("never", 1), ("rarely", 2), ("seldom", 2), ("once in a while", 3), ("occasionally", 3),
        ("few times a month", 3.5), ("monthly", 3.5), ("sometimes", 4), ("few times a week", 4.5), ("weekly", 4.5),
        ("often", 5), ("usually", 5.5), ("most days", 5.5), ("frequently", 6), ("daily", 6.5), ("every day", 6.5),
        ("almost always", 6.8), ("always", 7),
        // Impact / severity: No impact < Slight < Mild < Moderate < Severe (< Very severe < Extreme)
        ("no impact", 0.5), ("not at all", 0.5), ("none", 1), ("minimal", 2), ("slight", 3), ("mild", 4),
        ("low", 3), ("moderate", 5), ("medium", 5), ("high", 7), ("severe", 7), ("very severe", 8), ("extreme", 9),
        // Agreement (Likert)
        ("strongly disagree", 1), ("disagree", 2), ("neutral", 3), ("agree", 4), ("strongly agree", 5),
        // Quality
        ("very poor", 0.5), ("poor", 1), ("fair", 2), ("good", 3), ("very good", 4), ("excellent", 5),
        // Amount
        ("very low", 1), ("very high", 5),
        // Yes / maybe / no
        ("no", 1), ("maybe", 3), ("yes", 5),
    };

    private static readonly Dictionary<string, double> UnitDays = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sec"] = 1.0 / 86400, ["second"] = 1.0 / 86400, ["min"] = 1.0 / 1440, ["minute"] = 1.0 / 1440,
        ["h"] = 1.0 / 24, ["hr"] = 1.0 / 24, ["hour"] = 1.0 / 24,
        ["day"] = 1, ["week"] = 7, ["wk"] = 7, ["month"] = 30, ["mo"] = 30, ["year"] = 365, ["yr"] = 365,
    };

    private static readonly Regex NumberUnit = new(
        @"(\d+(?:\.\d+)?)\s*(years?|yrs?|months?|mos?|weeks?|wks?|days?|hours?|hrs?|minutes?|mins?|seconds?|secs?|h)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Zero-point words that can appear ALONGSIDE numeric ranges in the same
    // duration/frequency/impact variable (e.g. "Never" mixed with "1-6 months",
    // "No impact" mixed with a severity range) and always sort below every
    // real range — never itself parsed as a number.
    private static readonly HashSet<string> ZeroAnchors = new(StringComparer.OrdinalIgnoreCase)
    { "never", "none", "no impact", "not at all", "zero" };

    // Attempts to order the distinct values. Returns true with the ordered list
    // and a confidence ("High"/"Medium") when an unambiguous order exists.
    public static bool TryOrder(IReadOnlyList<string> distinct, out List<string> ordered, out string confidence, out string method)
    {
        ordered = new List<string>();
        confidence = "";
        method = "";
        if (distinct is null || distinct.Count < 2) return false;

        if (TryKey(distinct, NumericKey, out ordered)) { confidence = "High"; method = "numeric ranges"; return true; }
        if (TryKey(distinct, LexiconKey, out ordered)) { confidence = "High"; method = "recognized ordinal scale"; return true; }
        return false;
    }

    private static bool TryKey(IReadOnlyList<string> distinct, Func<string, double?> keyer, out List<string> ordered)
    {
        ordered = new List<string>();
        var keyed = new List<(string Value, double Key)>();
        foreach (var v in distinct)
        {
            var k = keyer(v);
            if (k is null) return false;            // one value not resolvable → whole method fails
            keyed.Add((v, k.Value));
        }
        // Distinct keys required — ties mean the order is ambiguous.
        if (keyed.Select(k => Math.Round(k.Key, 6)).Distinct().Count() != keyed.Count) return false;
        ordered = keyed.OrderBy(k => k.Key).Select(k => k.Value).ToList();
        return true;
    }

    private static double? LexiconKey(string value)
    {
        string s = Normalize(value);
        foreach (var (phrase, rank) in Lexicon)
            if (s == phrase) return rank;
        // First significant word (handles "never smoke", "daily use").
        string first = s.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        foreach (var (phrase, rank) in Lexicon)
            if (!phrase.Contains(' ') && first == phrase) return rank;
        return null;
    }

    private static double? NumericKey(string value)
    {
        string s = Normalize(value);
        if (ZeroAnchors.Contains(s)) return -1_000_000;   // always the floor of any numeric range scale

        int qualifier = 0;
        if (s.Contains("less than") || s.Contains("under") || s.Contains("below") || s.Contains("fewer than") || s.Contains("up to") || s.Contains("<"))
            qualifier = -1;
        else if (s.Contains("more than") || s.Contains("over") || s.Contains("above") || s.Contains("greater than") || s.Contains("at least") || s.Contains(">") || Regex.IsMatch(s, @"\d\s*\+"))
            qualifier = 1;

        // First pass: collect (number, unit) pairs and the value's first unit.
        var nums = new List<(double Num, double? Mult)>();
        double? valueUnitMult = null;
        foreach (Match m in NumberUnit.Matches(value))
        {
            if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double num)) continue;
            string unit = m.Groups[2].Success ? m.Groups[2].Value.TrimEnd('s') : "";
            double? mult = unit.Length > 0 && UnitDays.TryGetValue(unit, out double u) ? u : null;
            if (mult is not null && valueUnitMult is null) valueUnitMult = mult;
            nums.Add((num, mult));
        }
        if (nums.Count == 0) return null;

        // A number without its own unit inherits the value's unit ("1-6 months"
        // → the "1" is 1 month, not a bare 1), so cross-unit ranges sort right.
        var basals = nums.Select(n => n.Num * (n.Mult ?? valueUnitMult ?? 1)).ToList();
        double baseVal = basals.Min();   // lower bound of the range represents the category
        // Nudge by qualifier so "less than X" sorts just below X and "more than
        // X" just above, even when their base number equals a neighbour's.
        return baseVal * (1 + qualifier * 0.01) + qualifier * 1e-6;
    }

    private static string Normalize(string s)
    {
        s = (s ?? "").Trim().ToLowerInvariant();
        s = s.Replace('–', '-').Replace('—', '-');
        s = Regex.Replace(s, @"\s+", " ");
        return s;
    }
}

public static class MagicFixService
{
    // A continuous variable needs a non-numeric MAJORITY before we treat it as a
    // mis-typed category (rare non-numeric entries are handled elsewhere as
    // invalid values, not a type problem).
    private const double NonNumericMajorityShare = 0.50;
    // Above this many distinct categories a variable looks like free text, so we
    // do not offer a categorical/ordinal repair.
    private const int MaxRepairableCategories = 15;

    public static List<MagicFixProposal> BuildProposals(
        IReadOnlyList<ResearchVariable> variables, StatisticsDataset? data, StatisticsMatchInput match)
    {
        var proposals = new List<MagicFixProposal>();
        if (data is null || match is null) return proposals;   // no full dataset → nothing to repair locally

        foreach (var v in (variables ?? new List<ResearchVariable>()).Where(v => !string.IsNullOrWhiteSpace(v.VariableName)))
        {
            if (!match.VariableColumn.TryGetValue(v.Id, out string? col) || string.IsNullOrWhiteSpace(col)) continue;
            if (data.ColumnIndexOf(col) < 0) continue;

            var prep = StatisticsVariablePreparer.Prepare(v, data, col);
            var distinct = prep.DistinctMeaningful;
            if (distinct.Count == 0) continue;   // empty column → never fabricate; keep the blocker

            string type = (v.VariableType ?? "").Trim();
            string level = (v.MeasurementLevel ?? "").Trim();
            bool isContinuous = type.Equals("Continuous", StringComparison.OrdinalIgnoreCase)
                || (type.Equals("Numeric", StringComparison.OrdinalIgnoreCase) && level.Equals("Scale", StringComparison.OrdinalIgnoreCase));
            bool isBinary = type.Equals("Binary", StringComparison.OrdinalIgnoreCase);
            bool isOrdinal = type.Equals("Ordinal", StringComparison.OrdinalIgnoreCase) || level.Equals("Ordinal", StringComparison.OrdinalIgnoreCase);
            bool isCategorical = type.Equals("Categorical", StringComparison.OrdinalIgnoreCase);
            bool isText = type.Equals("Text", StringComparison.OrdinalIgnoreCase);

            bool hasCoding = prep.ValueLabelMap.Count > 0;
            var outside = OutsideValues(prep, distinct);
            bool orderable = TryOrderFull(distinct, out var orderedValues, out string fullCoding, out bool fullSetsCoding, out string method);

            string NumericNote(string prefix) => fullSetsCoding
                ? $"{prefix} Order inferred from {method}: {PreviewOrdered(orderedValues)}."
                : $"{prefix} The values are already numeric ({method}) and will sort correctly; no value labels are needed.";

            MagicFixProposal? p = null;

            // A. Continuous but the values are mostly non-numeric categories.
            if (isContinuous)
            {
                double numericShare = prep.MeaningfulValues.Count == 0 ? 0
                    : (double)prep.NumericValues.Count / prep.MeaningfulValues.Count;
                if (numericShare < NonNumericMajorityShare)
                    p = BuildTypeRepair(v, distinct, sourceLabel: "Continuous", categoricalIsHighConfidence: false);
                // else: genuinely numeric with a few stray entries → not a type repair.
            }
            // B. Binary but there are 3+ meaningful categories.
            else if (isBinary && distinct.Count > 2)
            {
                p = BuildTypeRepair(v, distinct, sourceLabel: "Binary", categoricalIsHighConfidence: true);
            }
            // C. Text or Categorical (not yet Ordinal) whose FULL set of values is
            //    orderable — e.g. duration ranges misclassified as Text/Category.
            //    Fires regardless of any existing (possibly incomplete or wrongly
            //    ordered) coding, so a mistyped variable is correctly promoted to
            //    Ordinal even if it already has some labels (Issue 3).
            else if ((isText || isCategorical) && !isOrdinal && orderable)
            {
                p = OrdinalRepair(v, type, level, orderedValues, fullSetsCoding, fullCoding,
                    NumericNote($"The values look like ordered categories ({method}), not free {(isText ? "text" : "categories")}."));
            }
            // D. Ordinal with no coding yet.
            else if (isOrdinal && !hasCoding && distinct.Count >= 2 && distinct.Count <= MaxRepairableCategories)
            {
                if (orderable)
                    p = OrdinalRepair(v, type, level, orderedValues, fullSetsCoding, fullCoding,
                        NumericNote("This ordinal variable has no value labels yet."));
                else
                    p = new MagicFixProposal
                    {
                        VariableId = v.Id, VariableName = v.VariableName.Trim(), Label = (v.QuestionLabel ?? "").Trim(),
                        CurrentType = type, CurrentLevel = level, ProposedType = type,
                        ObservedPreview = Preview(distinct), SetsCoding = false,
                        Confidence = "Low", Group = "manual", IsApplicable = false,
                        Explanation = "This ordinal variable has no value labels, and a safe order could not be inferred automatically. Please set the category order manually in the extraction sheet."
                    };
            }
            // E. Ordinal/Categorical WITH coding but observed values fall outside
            //    it — this is a WARNING-level repair, not just a blocker (Issue 1).
            //    When the full set is orderable, REGENERATE a complete, correctly
            //    ordered coding rather than only appending to the existing one —
            //    the existing coding may itself be incomplete or in the wrong
            //    order, and appending would preserve that mistake.
            else if ((isOrdinal || isCategorical) && hasCoding && outside.Count > 0)
            {
                if (orderable && orderedValues.All(x => StatisticsVariablePreparer.TryParseNumeric(x, out _)))
                {
                    // The values are already numeric, but the EXISTING coding is
                    // broken/incomplete for this scale (that is exactly why this
                    // branch fired). Unlike branches C/D — where no coding at all
                    // is the safest no-op-free choice for a fresh numeric ordinal
                    // — here doing nothing would leave the warning in place, so a
                    // "no repair needed" outcome is not an option. Replace the
                    // broken coding with self-identity labels (2 = 2, 3 = 3, …):
                    // every observed category becomes its own label, which both
                    // clears the "outside labels" problem and never shifts.
                    string identity = string.Join(", ", orderedValues.Select(x => $"{x.Trim()} = {x.Trim()}"));
                    p = OrdinalRepair(v, type, level, orderedValues, setsCoding: true, identity,
                        $"{outside.Count} observed value(s) are outside the current labels, and the existing coding does not match these numeric categories. Replace it with self-identity labels ({PreviewOrdered(orderedValues)}) so each numeric category is its own label.");
                }
                else if (orderable)
                {
                    p = OrdinalRepair(v, type, level, orderedValues, fullSetsCoding, fullCoding,
                        NumericNote($"{outside.Count} observed value(s) are outside the current labels."));
                }
                else if (TryBuildAddedLabels(prep.ValueLabelMap, outside, out string appended))
                {
                    p = new MagicFixProposal
                    {
                        VariableId = v.Id, VariableName = v.VariableName.Trim(), Label = (v.QuestionLabel ?? "").Trim(),
                        CurrentType = type, CurrentLevel = level, ProposedType = type,
                        ObservedPreview = Preview(outside), SetsCoding = true, ProposedCoding = appended,
                        Confidence = "High", Group = "safe", IsApplicable = true,
                        Explanation = $"{outside.Count} observed value(s) are not in this variable's value labels. Adding them keeps the coding complete without changing the variable type."
                    };
                }
            }

            if (p is not null)
            {
                p.Accepted = p.DefaultSelected;
                proposals.Add(p);
            }
        }

        return proposals
            .OrderBy(p => p.GroupOrder)
            .ThenBy(p => p.VariableName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Observed distinct values not covered by the variable's value labels
    // (matched against both the code and the label text, case-insensitively).
    private static List<string> OutsideValues(VariablePreparation prep, List<string> distinct)
        => distinct.Where(dv => !prep.ValueLabelMap.ContainsKey(dv)
                && !prep.ValueLabelMap.Values.Any(lbl => string.Equals(lbl, dv, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    // True when the full distinct set has an unambiguous order. `coding` is a
    // generated value-label string, and `setsCoding` reports whether it should
    // actually be applied — false when the values are already plain numbers
    // (they sort correctly on their own and must NOT receive a positional
    // coding; see TryBuildCoding). Order can still be "resolved" (for type
    // promotion) even when no coding is needed.
    private static bool TryOrderFull(List<string> distinct, out List<string> ordered, out string coding, out bool setsCoding, out string method)
    {
        ordered = new List<string>();
        coding = "";
        setsCoding = false;
        method = "";
        if (distinct.Count < 2 || distinct.Count > MaxRepairableCategories) return false;
        if (!MagicFixOrdering.TryOrder(distinct, out ordered, out _, out method)) return false;
        setsCoding = TryBuildCoding(ordered, out coding);
        return true;
    }

    // A High-confidence "safe" Ordinal repair with a (possibly empty, when the
    // values are already numeric) generated ordered coding.
    private static MagicFixProposal OrdinalRepair(ResearchVariable v, string type, string level, List<string> ordered, bool setsCoding, string coding, string explanation)
        => new()
        {
            VariableId = v.Id, VariableName = v.VariableName.Trim(), Label = (v.QuestionLabel ?? "").Trim(),
            CurrentType = type, CurrentLevel = level, ProposedType = "Ordinal",
            ObservedPreview = Preview(ordered), SetsCoding = setsCoding, ProposedCoding = setsCoding ? coding : "",
            Confidence = "High", Group = "safe", IsApplicable = true, Explanation = explanation
        };

    // Builds a type repair for a variable whose values are discrete categories:
    // Ordinal (with generated coding) when an order is obvious, otherwise
    // Categorical/Nominal.
    private static MagicFixProposal? BuildTypeRepair(ResearchVariable v, List<string> distinct, string sourceLabel, bool categoricalIsHighConfidence)
    {
        if (distinct.Count < 2 || distinct.Count > MaxRepairableCategories) return null;

        string type = (v.VariableType ?? "").Trim();
        string level = (v.MeasurementLevel ?? "").Trim();
        var baseProp = new MagicFixProposal
        {
            VariableId = v.Id, VariableName = v.VariableName.Trim(), Label = (v.QuestionLabel ?? "").Trim(),
            CurrentType = type, CurrentLevel = level, ObservedPreview = Preview(distinct)
        };

        if (MagicFixOrdering.TryOrder(distinct, out var ordered, out _, out string method))
        {
            bool setsCoding = TryBuildCoding(ordered, out string coding);
            baseProp.ProposedType = "Ordinal";
            baseProp.SetsCoding = setsCoding;
            baseProp.ProposedCoding = setsCoding ? coding : "";
            baseProp.Confidence = "High";
            baseProp.Group = "safe";
            baseProp.IsApplicable = true;
            baseProp.Explanation = setsCoding
                ? $"The values are {sourceLabel.ToLowerInvariant()} in the sheet but look like ordered categories in the data. Suggest Ordinal with an order inferred from {method}: {PreviewOrdered(ordered)}."
                : $"The values are {sourceLabel.ToLowerInvariant()} in the sheet but are already numeric ({method}) and will sort correctly as Ordinal: {PreviewOrdered(ordered)}. No value labels are needed.";
            return baseProp;
        }

        // No obvious order → Categorical / Nominal (never invents an order).
        baseProp.ProposedType = "Categorical";
        baseProp.SetsCoding = false;
        baseProp.Confidence = categoricalIsHighConfidence ? "High" : "Medium";
        baseProp.Group = categoricalIsHighConfidence ? "safe" : "review";
        baseProp.IsApplicable = true;
        baseProp.Explanation = sourceLabel == "Binary"
            ? $"This binary variable has {distinct.Count} categories, so it cannot be analyzed as binary. A clear order was not found, so suggest Categorical (nominal). Review whether an order applies."
            : $"The values are continuous in the sheet but look like {distinct.Count} discrete categories in the data. A clear order was not found, so suggest Categorical (nominal). Review whether an order applies.";
        return baseProp;
    }

    // "1 = v1, 2 = v2, …" ascending. Skips generation when a value contains a
    // separator the value-label parser would split on (correctness over guess).
    //
    // CRITICAL: never generates a positional "i+1 = value" coding when the
    // ordered values are ALREADY plain numbers (e.g. a 2–9 severity scale).
    // Numeric values already sort correctly on their own (see the engine's
    // numeric-ascending path) and do NOT need value labels — and must never
    // receive them, because positional numbering silently SHIFTS the display
    // whenever the raw values don't start at 1 (a "2" would get the label "3",
    // a "9" would get no label at all). Skipping coding here is the fix.
    private static bool TryBuildCoding(List<string> orderedValues, out string coding)
    {
        coding = "";
        if (orderedValues.Any(v => v.Contains(',') || v.Contains(';') || v.Contains('\n'))) return false;
        if (orderedValues.All(v => StatisticsVariablePreparer.TryParseNumeric(v, out _))) return false;
        coding = string.Join(", ", orderedValues.Select((v, i) => $"{i + 1} = {v.Trim()}"));
        return coding.Length > 0;
    }

    private static bool TryBuildAddedLabels(Dictionary<string, string> existing, List<string> outside, out string coding)
    {
        coding = "";
        if (outside.Any(v => v.Contains(',') || v.Contains(';') || v.Contains('\n'))) return false;
        var parts = existing.Select(kv => $"{kv.Key} = {kv.Value}").ToList();
        int next = existing.Keys.Select(k => int.TryParse(k, out int n) ? n : 0).DefaultIfEmpty(0).Max() + 1;
        foreach (var o in outside) parts.Add($"{next++} = {o.Trim()}");
        coding = string.Join(", ", parts);
        return true;
    }

    private static string Preview(IReadOnlyList<string> values, int max = 5)
    {
        var shown = values.Take(max).Select(Sanitize);
        string s = string.Join(", ", shown.Select(x => "“" + x + "”"));
        return values.Count > max ? s + $" and {values.Count - max} more" : s;
    }

    private static string PreviewOrdered(IReadOnlyList<string> ordered, int max = 5)
    {
        var shown = ordered.Take(max).Select(Sanitize);
        string s = string.Join(" < ", shown);
        return ordered.Count > max ? s + " < …" : s;
    }

    private static string Sanitize(string s)
    {
        s = (s ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length <= 40 ? s : s.Substring(0, 40).TrimEnd() + "…";
    }
}
