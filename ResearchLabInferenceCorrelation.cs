using System.Globalization;
using System.Text;

namespace AIFlashcardMaker;

// ===========================================================================
// Research Lab — Phase 4B Part 2 (MVP-3): deterministic SPEARMAN correlation.
//
// Spearman's ρ for two rankable variables (Ordinal/Continuous). Builds on the
// MVP-1/2 engines and shares InferenceMath.
//
// HARD RULES (audit-critical):
//   * Deterministic C# only (System.Math). Same inputs → bit-identical output.
//   * NO randomness. The p-value is the standard t approximation
//     (t = ρ·√((n−2)/(1−ρ²)), df = n−2) via the shared Student-t tail. NO
//     exact/permutation Spearman.
//   * ρ is computed as the correlation of the RANKS (Pearson-on-ranks), which
//     is Spearman's definition and is correct with ties. This is NOT a Pearson
//     correlation of raw values and no Pearson test is exposed.
//   * NO AI, HTTP, network, logging, or file I/O. Consumes already-loaded,
//     in-memory data; raw participant rows never leave this device / reach a
//     log or AI.
//   * Part 2 NEVER chooses a test. It executes ONLY the Spearman recommendation
//     for an eligible pairing (both sides rankable). The engine re-checks
//     eligibility, so no arbitrary test can be run.
//   * NO Pearson / regression / t-test / ANOVA / odds ratio / confidence
//     interval / partial correlation / Kendall's tau here.
//   * No WPF dependency — everything here is headless-testable.
// ===========================================================================

public enum SpearmanStatus
{
    Computed,       // a valid ρ + p-value were produced
    CannotCompute,  // inputs valid-shaped but a guardrail blocks a reliable result
    NotRunnable     // the pairing is not eligible for a Spearman correlation
}

public sealed class SpearmanResult : IInferenceExportable
{
    public string XName { get; set; } = "";
    public string XDisplay { get; set; } = "";
    public string XKind { get; set; } = "";      // "Ordinal" | "Continuous"
    public string YName { get; set; } = "";
    public string YDisplay { get; set; } = "";
    public string YKind { get; set; } = "";
    public string PairTypeDisplay { get; set; } = "";

    public SpearmanStatus Status { get; set; } = SpearmanStatus.NotRunnable;
    public string StatusReason { get; set; } = "";
    public string TestUsed { get; set; } = "Spearman correlation";

    // True when BOTH variables are continuous: Spearman is then the robust
    // ALTERNATIVE to the recommended (but un-computed) Pearson correlation.
    public bool IsRobustAlternative { get; set; }

    public int PairN { get; set; }
    public int DroppedForMissing { get; set; }
    public int DroppedInvalid { get; set; }

    public double? Rho { get; set; }
    public double? TStatistic { get; set; }
    public int DegreesOfFreedom { get; set; }
    public double? PValue { get; set; }

    public bool TiesPresent { get; set; }
    public string TieNote { get; set; } = "";
    public List<string> Assumptions { get; set; } = new();
    public List<string> Notes { get; set; } = new();

    public bool AiInvolved => false;
    public bool Computed => Status == SpearmanStatus.Computed;
    public string PValueDisplay => PValue is null ? "not calculated" : InferenceMath.FormatPValue(PValue.Value);
    public string RhoDisplay => Rho is null ? "—" : InferenceMath.FormatNumber(Rho, 3);
    public string GeneratedDisplay => DateTime.UtcNow.ToLocalTime().ToString("MMM d, yyyy · h:mm tt", CultureInfo.InvariantCulture);

    // Heuristic strength band (labelled a heuristic — never a hard verdict).
    public string StrengthBand
    {
        get
        {
            if (Rho is not { } r) return "";
            double a = Math.Abs(r);
            string s = a < 0.10 ? "negligible" : a < 0.30 ? "weak" : a < 0.50 ? "moderate" : a < 0.70 ? "strong" : "very strong";
            return $"{s} {(r < 0 ? "negative" : "positive")} monotonic association (heuristic)";
        }
    }

    // IInferenceExportable.
    public string ResultTitle => $"{XDisplay}  vs  {YDisplay}";
    public string ToPlainText() => SpearmanExport.BuildPlainText(this);
    public string ToCsv() => SpearmanExport.BuildCsv(this);
}

// ---------------------------------------------------------------------------
// The deterministic Spearman engine.
// ---------------------------------------------------------------------------
public static class SpearmanCorrelationEngine
{
    public const int MinPairs = 10;   // large-sample-approximation floor (no exact test this phase)

    public static bool IsRunnable(TestRecommendation? rec) => rec is not null && rec.CanComputeSpearman;

    private static bool IsRankableKind(string k) => k == "Ordinal" || k == "Continuous";

    public static SpearmanResult Compute(
        TestRecommendation rec,
        ResearchVariable? outcome,
        ResearchVariable? predictor,
        StatisticsDataset? data,
        StatisticsMatchInput? match)
    {
        var result = new SpearmanResult { PairTypeDisplay = rec?.PairTypeDisplay ?? "" };
        result.Notes.Add("Computed locally on this device by deterministic C# code. No AI was used to select or calculate this test.");
        result.Notes.Add("Only the aggregate coefficient, sample size, and p-value are shown or exported — no individual participant rows.");

        // --- Guard 1: eligibility. ------------------------------------------
        if (!IsRunnable(rec))
        {
            result.Status = SpearmanStatus.NotRunnable;
            result.StatusReason = "This pairing is not eligible for a Spearman correlation. Both variables must be ordinal or continuous, and the plan must be ready to plan or need assumption review.";
            return result;
        }
        if (outcome is null || predictor is null || data is null || data.RowCount == 0 || match is null)
        {
            result.Status = SpearmanStatus.NotRunnable;
            result.StatusReason = "The dataset or matched variables were not available to compute this test.";
            return result;
        }

        // Correlation is symmetric — X/Y labelling is display-only.
        var xVar = outcome; var yVar = predictor;
        string xKind = rec!.OutcomeKind, yKind = rec.PredictorKind;
        if (!IsRankableKind(xKind) || !IsRankableKind(yKind))
        {
            result.Status = SpearmanStatus.NotRunnable;
            result.StatusReason = "Both variables must be ordinal or continuous for a Spearman correlation.";
            return result;
        }

        result.XName = xVar.VariableName.Trim(); result.XDisplay = Display(xVar); result.XKind = xKind;
        result.YName = yVar.VariableName.Trim(); result.YDisplay = Display(yVar); result.YKind = yKind;
        result.IsRobustAlternative = xKind == "Continuous" && yKind == "Continuous";
        if (result.IsRobustAlternative)
            result.Notes.Insert(0, "Running the robust alternative. The primary recommendation (Pearson correlation) is not computed in this version.");

        int xIdx = data.ColumnIndexOf(ResolveColumn(xVar, match));
        int yIdx = data.ColumnIndexOf(ResolveColumn(yVar, match));
        if (xIdx < 0 || yIdx < 0)
        {
            result.Status = SpearmanStatus.NotRunnable;
            result.StatusReason = "The matched dataset columns for these variables could not be found.";
            return result;
        }

        // --- Assemble aligned (xRaw, yRaw) pairs (listwise deletion). --------
        var rawPairs = new List<(string X, string Y)>();
        for (int r = 0; r < data.RowCount; r++)
        {
            string xv = data.Cell(r, xIdx).Trim();
            string yv = data.Cell(r, yIdx).Trim();
            if (StatisticsMissingTokens.IsMissing(xv) || StatisticsMissingTokens.IsMissing(yv))
            {
                result.DroppedForMissing++;
                continue;
            }
            rawPairs.Add((xv, yv));
        }

        // --- Resolve ordinal orders (from the non-missing pairs). ------------
        Dictionary<string, int>? xOrder = null, yOrder = null;
        if (xKind == "Ordinal" && !TryResolveOrdinalIndex(rawPairs.Select(p => p.X), xVar, out xOrder))
        {
            result.Status = SpearmanStatus.CannotCompute;
            result.StatusReason = $"The ordinal variable “{result.XDisplay}” has no resolvable category order. Define its order (via coding / value labels or Magic Fix) before running a correlation.";
            AddMethodNotes(result);
            return result;
        }
        if (yKind == "Ordinal" && !TryResolveOrdinalIndex(rawPairs.Select(p => p.Y), yVar, out yOrder))
        {
            result.Status = SpearmanStatus.CannotCompute;
            result.StatusReason = $"The ordinal variable “{result.YDisplay}” has no resolvable category order. Define its order (via coding / value labels or Magic Fix) before running a correlation.";
            AddMethodNotes(result);
            return result;
        }

        // --- Convert to numeric keys; drop pairs where either value is invalid.
        var xs = new List<double>();
        var ys = new List<double>();
        foreach (var (xr, yr) in rawPairs)
        {
            if (!TryKey(xr, xKind, xOrder, out double xk) || !TryKey(yr, yKind, yOrder, out double yk))
            {
                result.DroppedInvalid++;
                continue;
            }
            xs.Add(xk); ys.Add(yk);
        }
        result.PairN = xs.Count;

        // --- Guard 2: enough complete pairs for the approximation. ----------
        if (result.PairN < MinPairs)
        {
            result.Status = SpearmanStatus.CannotCompute;
            result.StatusReason = $"There are only {result.PairN} complete pairs; at least {MinPairs} are needed for the large-sample p-value approximation. An exact small-sample test is not available in this version.";
            AddMethodNotes(result);
            return result;
        }

        // --- Rank each variable (average ranks for ties) → Pearson-on-ranks. -
        var rx = RankInferenceEngine.AverageRanks(xs.ToArray(), out double xTie);
        var ry = RankInferenceEngine.AverageRanks(ys.ToArray(), out double yTie);
        result.TiesPresent = xTie > 0 || yTie > 0;

        int n = result.PairN;
        double mx = rx.Average(), my = ry.Average();
        double cov = 0, vx = 0, vy = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = rx[i] - mx, dy = ry[i] - my;
            cov += dx * dy; vx += dx * dx; vy += dy * dy;
        }

        // --- Guard 3: both variables must vary (else ρ is undefined). -------
        if (vx <= 0 || vy <= 0)
        {
            result.Status = SpearmanStatus.CannotCompute;
            result.StatusReason = "One of the variables has no variation (all values are tied), so a correlation cannot be computed.";
            AddMethodNotes(result);
            return result;
        }

        double rho = Math.Clamp(cov / Math.Sqrt(vx * vy), -1.0, 1.0);
        result.Rho = rho;

        // --- t approximation for the p-value (df = n − 2). ------------------
        // Denominator is floored so a perfect ±1 correlation yields a finite,
        // very large t → p ≈ 0 → displayed as "< .001" (never NaN/Infinity).
        int df = n - 2;
        double denom = Math.Max(1.0 - rho * rho, 1e-12);
        double t = rho * Math.Sqrt(df / denom);
        result.TStatistic = t;
        result.DegreesOfFreedom = df;
        result.PValue = InferenceMath.StudentTTwoSidedP(t, df);
        result.Status = SpearmanStatus.Computed;

        result.TieNote = result.TiesPresent
            ? "Tied values received average ranks; ρ is the correlation of the ranks (tie-corrected)."
            : "No tied values were present, so no tie adjustment was needed.";
        AddAssumptions(result);
        AddMethodNotes(result);
        return result;
    }

    // Continuous → parsed number; Ordinal → its category's resolved index.
    private static bool TryKey(string raw, string kind, Dictionary<string, int>? order, out double key)
    {
        if (kind == "Continuous") return StatisticsVariablePreparer.TryParseNumeric(raw, out key);
        key = 0;
        if (order is null) return false;
        if (order.TryGetValue(raw.Trim().ToLowerInvariant(), out int idx)) { key = idx; return true; }
        return false;
    }

    // ---- Ordinal order resolution (duplicated from the rank engine on purpose
    // to keep the committed rank engine untouched). Same precedence as the
    // descriptive engine: canonical inference → value-label order → all-numeric
    // ascending → unresolved. ------------------------------------------------
    private static bool TryResolveOrdinalIndex(
        IEnumerable<string> rawValues, ResearchVariable v, out Dictionary<string, int>? indexByLowerKey)
    {
        indexByLowerKey = null;
        var labels = StatisticsVariablePreparer.ParseValueLabels(v.ValueLabels, v.Coding);

        var groups = rawValues
            .Select(x => x.Trim())
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.GroupBy(x => x, StringComparer.Ordinal)
                          .OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.Ordinal)
                          .First().Key)
            .ToList();
        if (groups.Count == 0) return false;

        List<string> ordered;
        bool allNumeric = groups.All(g => StatisticsVariablePreparer.TryParseNumeric(g, out _));

        if (MagicFixOrdering.TryOrder(groups, out var canonical, out _, out _))
        {
            ordered = canonical;
        }
        else if (labels.Count > 0)
        {
            var labelOrder = labels.Keys.ToList();
            int OrderOf(string val)
            {
                for (int i = 0; i < labelOrder.Count; i++)
                    if (string.Equals(labelOrder[i], val, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(labels[labelOrder[i]], val, StringComparison.OrdinalIgnoreCase))
                        return i;
                return int.MaxValue;
            }
            if (groups.Any(g => OrderOf(g) == int.MaxValue)) return false;
            ordered = groups.OrderBy(OrderOf).ToList();
        }
        else if (allNumeric)
        {
            ordered = groups.OrderBy(g => { StatisticsVariablePreparer.TryParseNumeric(g, out double d); return d; }).ToList();
        }
        else
        {
            return false;
        }

        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < ordered.Count; i++)
            map[ordered[i].Trim().ToLowerInvariant()] = i;
        foreach (var g in groups)
            if (!map.ContainsKey(g.Trim().ToLowerInvariant())) return false;
        indexByLowerKey = map;
        return true;
    }

    private static void AddAssumptions(SpearmanResult r)
    {
        r.Assumptions.Add($"At least {MinPairs} complete pairs: met (N = {r.PairN}).");
        r.Assumptions.Add("Monotonic relationship: assumed (Spearman measures monotonic, not linear, association).");
        r.Assumptions.Add("Independent observations: assumed (study-design level; not verifiable from the data).");
        if (r.IsRobustAlternative)
            r.Assumptions.Add("This is the robust alternative to the recommended Pearson correlation, which is not computed in this version.");
    }

    private static void AddMethodNotes(SpearmanResult r)
    {
        r.Notes.Add("Spearman's ρ is the Pearson correlation of the ranked values; tied values receive average ranks (this is Spearman's definition, not a Pearson correlation of the raw values).");
        r.Notes.Add("p-value uses the t approximation: t = ρ·√((n−2)/(1−ρ²)), df = n−2, two-sided from the Student-t distribution.");
        r.Notes.Add("Rows missing either variable are excluded pairwise. p-values are never shown as 0; values below .001 are shown as \"< .001\". Full precision is kept internally.");
        r.Notes.Add("No Pearson correlation, confidence interval, regression, partial correlation, or exact small-sample test is calculated in this version.");
    }

    private static string ResolveColumn(ResearchVariable v, StatisticsMatchInput match)
        => match.VariableColumn.TryGetValue(v.Id, out string? col) ? col : "";

    private static string Display(ResearchVariable v)
    {
        string name = v.VariableName.Trim();
        string label = (v.QuestionLabel ?? "").Trim();
        if (label.Length > 0 && !string.Equals(label, name, StringComparison.OrdinalIgnoreCase))
            return $"{name} ({Shorten(label, 60)})";
        return name.Contains('_') ? Shorten(Prettify(name), 70) : name;
    }

    private static readonly string[] QuestionStarters =
    { "do ", "does ", "did ", "is ", "are ", "was ", "were ", "has ", "have ", "had ", "can ", "could ", "will ", "would ", "should " };

    private static string Prettify(string name)
    {
        string s = (name ?? "").Trim().Replace('_', ' ');
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        if (s.Length == 0) return s;
        s = char.ToUpperInvariant(s[0]) + s.Substring(1);
        string lower = s.ToLowerInvariant();
        if (QuestionStarters.Any(q => lower.StartsWith(q)) && !s.EndsWith("?") && !s.EndsWith(".")) s += "?";
        return s;
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s.Substring(0, max).TrimEnd() + "…";
}

// ---------------------------------------------------------------------------
// Export: plain text and CSV of a computed Spearman result. Aggregate only —
// coefficient, N, statistic, p-value, and notes. No participant-level data.
// ---------------------------------------------------------------------------
public static class SpearmanExport
{
    public static string BuildPlainText(SpearmanResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SPEARMAN CORRELATION RESULT");
        sb.AppendLine($"Generated: {r.GeneratedDisplay}");
        sb.AppendLine($"Variable X: {r.XDisplay}  ({r.XKind})");
        sb.AppendLine($"Variable Y: {r.YDisplay}  ({r.YKind})");
        sb.AppendLine($"Complete pairs (N): {r.PairN}   (excluded for missing: {r.DroppedForMissing}; invalid values dropped: {r.DroppedInvalid})");
        sb.AppendLine(new string('=', 78));

        if (r.Status == SpearmanStatus.NotRunnable)
        {
            sb.AppendLine("This pairing could not be run.");
            sb.AppendLine(r.StatusReason);
            AppendNotes(sb, r);
            return sb.ToString();
        }

        sb.AppendLine();
        if (r.Status == SpearmanStatus.CannotCompute)
        {
            sb.AppendLine("RESULT: Cannot compute a reliable correlation — needs review.");
            sb.AppendLine(r.StatusReason);
        }
        else
        {
            sb.AppendLine($"Test used: {r.TestUsed}");
            sb.AppendLine($"Spearman's rho: {r.RhoDisplay}");
            sb.AppendLine($"Strength: {r.StrengthBand}");
            sb.AppendLine($"Statistic: t = {InferenceMath.FormatNumber(r.TStatistic, 3)}   df = {r.DegreesOfFreedom}");
            sb.AppendLine($"p-value: {r.PValueDisplay}");
            sb.AppendLine($"Tie handling: {r.TieNote}");
        }

        if (r.Assumptions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Assumptions:");
            foreach (var a in r.Assumptions) sb.AppendLine("  - " + a);
        }

        AppendNotes(sb, r);
        return sb.ToString();
    }

    private static void AppendNotes(StringBuilder sb, SpearmanResult r)
    {
        sb.AppendLine();
        sb.AppendLine("Notes:");
        foreach (var n in r.Notes) sb.AppendLine("  • " + n);
    }

    public static string BuildCsv(SpearmanResult r)
    {
        var sb = new StringBuilder();
        string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

        sb.AppendLine(string.Join(",", new[]
        {
            "X", "XKind", "Y", "YKind", "Status", "TestUsed", "PairN", "MissingExcluded", "InvalidDropped",
            "Rho", "t", "df", "p", "TiesPresent", "RobustAlternative", "AiInvolved"
        }.Select(Q)));

        sb.AppendLine(string.Join(",", new[]
        {
            Q(r.XDisplay), Q(r.XKind), Q(r.YDisplay), Q(r.YKind), Q(r.Status.ToString()), Q(r.TestUsed),
            r.PairN.ToString(CultureInfo.InvariantCulture), r.DroppedForMissing.ToString(CultureInfo.InvariantCulture),
            r.DroppedInvalid.ToString(CultureInfo.InvariantCulture),
            Q(InferenceMath.FormatNumber(r.Rho, 4)), Q(InferenceMath.FormatNumber(r.TStatistic, 4)),
            r.DegreesOfFreedom.ToString(CultureInfo.InvariantCulture),
            Q(r.PValue is null ? "not calculated" : InferenceMath.FormatPValue(r.PValue.Value)),
            Q(r.TiesPresent ? "yes" : "no"), Q(r.IsRobustAlternative ? "yes" : "no"), Q("no")
        }));
        return sb.ToString();
    }
}
