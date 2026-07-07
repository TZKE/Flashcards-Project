using System.Globalization;
using System.Text;

namespace AIFlashcardMaker;

// ===========================================================================
// Research Lab — Phase 4B Part 2 (MVP-2): deterministic RANK-BASED inference.
//
// Mann-Whitney U (a ranked variable vs a 2-group factor) and Kruskal-Wallis
// (a ranked variable vs a 3+-group factor). Builds on the MVP-1 categorical
// engine and shares InferenceMath.
//
// HARD RULES (audit-critical):
//   * Deterministic C# only (System.Math). Same inputs → bit-identical output.
//   * NO randomness. p-values are closed-form approximations: Mann-Whitney via
//     the tie-corrected normal approximation with continuity correction;
//     Kruskal-Wallis via the tie-corrected chi-square upper tail (reusing the
//     already-validated InferenceMath). NO exact small-sample enumeration.
//   * NO AI, HTTP, network, logging, or file I/O. Consumes already-loaded,
//     in-memory data; raw participant rows never leave this device or reach a
//     log / AI.
//   * Part 2 NEVER chooses a test. It executes ONLY the rank test that Part 1
//     (TestRecommendationEngine) recommended for an eligible pairing — the
//     ranked variable is detected by KIND (Ordinal/Continuous), the grouping
//     variable is the Binary/Categorical side, regardless of outcome/predictor
//     role, and both orientations are handled.
//   * NO t-test / ANOVA / Pearson / Spearman / odds ratio / confidence
//     interval / regression / post-hoc (Dunn) / exact MWU / Wilcoxon
//     signed-rank / multiple-comparison correction here.
//   * No WPF dependency — everything here is headless-testable.
// ===========================================================================

public enum RankTestStatus
{
    Computed,       // a valid p-value was produced (Mann-Whitney or Kruskal-Wallis)
    CannotCompute,  // inputs valid-shaped but a guardrail blocks a reliable result
    NotRunnable     // the pairing is not eligible for a rank test
}

// One group's aggregate rank summary (never any participant rows).
public sealed class RankGroupSummary
{
    public string Label { get; set; } = "";
    public int N { get; set; }
    public double RankSum { get; set; }
    public double MeanRank { get; set; }
}

public sealed class RankTestResult : IInferenceExportable
{
    public string RankedName { get; set; } = "";
    public string RankedDisplay { get; set; } = "";
    public string RankedKind { get; set; } = "";        // "Ordinal" | "Continuous"
    public string GroupingName { get; set; } = "";
    public string GroupingDisplay { get; set; } = "";
    public string PairTypeDisplay { get; set; } = "";

    public RankTestStatus Status { get; set; } = RankTestStatus.NotRunnable;
    public string StatusReason { get; set; } = "";
    public string TestUsed { get; set; } = "Not computed";

    // True when the ranked variable is CONTINUOUS: the rank test is then the
    // robust ALTERNATIVE to the recommended (but un-computed) t-test / ANOVA.
    public bool IsRobustAlternative { get; set; }

    public int ValidN { get; set; }
    public int DroppedForMissing { get; set; }
    public int DroppedInvalid { get; set; }

    public List<RankGroupSummary> Groups { get; set; } = new();

    // Mann-Whitney (2 groups).
    public double? U1 { get; set; }
    public double? U2 { get; set; }
    public double? U { get; set; }
    public double? Z { get; set; }

    // Kruskal-Wallis (3+ groups).
    public double? H { get; set; }
    public int DegreesOfFreedom { get; set; }

    public double? PValue { get; set; }                 // full precision, headline
    public string EffectName { get; set; } = "";        // "Rank-biserial correlation" | "Epsilon squared"
    public double? EffectValue { get; set; }
    public string EffectDirectionNote { get; set; } = "";

    public bool TieCorrectionApplied { get; set; }
    public string TieNote { get; set; } = "";
    public List<string> Assumptions { get; set; } = new();
    public List<string> Notes { get; set; } = new();

    public bool AiInvolved => false;
    public bool Computed => Status == RankTestStatus.Computed;
    public string PValueDisplay => PValue is null ? "not calculated" : InferenceMath.FormatPValue(PValue.Value);
    public string GeneratedDisplay => DateTime.UtcNow.ToLocalTime().ToString("MMM d, yyyy · h:mm tt", CultureInfo.InvariantCulture);

    // IInferenceExportable.
    public string ResultTitle => $"{RankedDisplay}  by  {GroupingDisplay}";
    public string ToPlainText() => RankInferenceExport.BuildPlainText(this);
    public string ToCsv() => RankInferenceExport.BuildCsv(this);
}

// ---------------------------------------------------------------------------
// The deterministic rank-inference engine.
// ---------------------------------------------------------------------------
public static class RankInferenceEngine
{
    public const int MinGroupSize = 5;   // large-sample-approximation floor (no exact test this phase)

    public static bool IsRunnable(TestRecommendation? rec) => rec is not null && rec.CanComputeRank;

    private static bool IsRankableKind(string k) => k == "Ordinal" || k == "Continuous";
    private static bool IsGroupingKind(string k) => k == "Binary" || k == "Categorical";

    public static RankTestResult Compute(
        TestRecommendation rec,
        ResearchVariable? outcome,
        ResearchVariable? predictor,
        StatisticsDataset? data,
        StatisticsMatchInput? match)
    {
        var result = new RankTestResult { PairTypeDisplay = rec?.PairTypeDisplay ?? "" };
        result.Notes.Add("Computed locally on this device by deterministic C# code. No AI was used to select or calculate this test.");
        result.Notes.Add("Only aggregate group rank summaries are shown or exported — no individual participant rows.");

        // --- Guard 1: eligibility. ------------------------------------------
        if (!IsRunnable(rec))
        {
            result.Status = RankTestStatus.NotRunnable;
            result.StatusReason = "This pairing is not eligible for a rank test. Only an ordinal/continuous variable compared across a binary/categorical grouping that is ready to plan or needs assumption review can be computed.";
            return result;
        }
        if (outcome is null || predictor is null || data is null || data.RowCount == 0 || match is null)
        {
            result.Status = RankTestStatus.NotRunnable;
            result.StatusReason = "The dataset or matched variables were not available to compute this test.";
            return result;
        }

        // --- Orientation: ranked = ordinal/continuous side; grouping = the
        // binary/categorical side. Detected by KIND, not by role. -------------
        ResearchVariable ranked, grouping;
        string rankedKind;
        if (IsRankableKind(rec!.OutcomeKind) && IsGroupingKind(rec.PredictorKind))
        {
            ranked = outcome; grouping = predictor; rankedKind = rec.OutcomeKind;
        }
        else if (IsRankableKind(rec.PredictorKind) && IsGroupingKind(rec.OutcomeKind))
        {
            ranked = predictor; grouping = outcome; rankedKind = rec.PredictorKind;
        }
        else
        {
            result.Status = RankTestStatus.NotRunnable;
            result.StatusReason = "This pairing does not have one ordinal/continuous variable and one grouping variable.";
            return result;
        }

        result.RankedName = ranked.VariableName.Trim();
        result.RankedDisplay = Display(ranked);
        result.RankedKind = rankedKind;
        result.GroupingName = grouping.VariableName.Trim();
        result.GroupingDisplay = Display(grouping);
        result.IsRobustAlternative = rankedKind == "Continuous";
        if (result.IsRobustAlternative)
            result.Notes.Insert(0, "Running the robust alternative. The primary recommendation (t-test / ANOVA) is not computed in this version.");

        var rankedPrep = StatisticsVariablePreparer.Prepare(ranked, data, ResolveColumn(ranked, match));
        var groupPrep = StatisticsVariablePreparer.Prepare(grouping, data, ResolveColumn(grouping, match));
        int rIdx = data.ColumnIndexOf(rankedPrep.MatchedColumn);
        int gIdx = data.ColumnIndexOf(groupPrep.MatchedColumn);
        if (rIdx < 0 || gIdx < 0)
        {
            result.Status = RankTestStatus.NotRunnable;
            result.StatusReason = "The matched dataset columns for these variables could not be found.";
            return result;
        }

        // --- Assemble aligned (rankedRaw, groupRaw) pairs (listwise). --------
        var pairs = new List<(string RankedRaw, string GroupKey, string GroupDisplay)>();
        var groupAcc = new GroupAccumulator(StatisticsVariablePreparer.ParseValueLabels(grouping.ValueLabels, grouping.Coding));
        for (int r = 0; r < data.RowCount; r++)
        {
            string rv = data.Cell(r, rIdx).Trim();
            string gv = data.Cell(r, gIdx).Trim();
            if (StatisticsMissingTokens.IsMissing(rv) || StatisticsMissingTokens.IsMissing(gv))
            {
                result.DroppedForMissing++;
                continue;
            }
            groupAcc.Observe(gv);
            pairs.Add((rv, gv.Trim(), gv));
        }

        // --- Convert ranked raw values to numeric sort keys. -----------------
        // Continuous: parsed number; invalid values are dropped (counted).
        // Ordinal: mapped to its category's position in the resolved order;
        // if the order cannot be resolved, refuse rather than guess.
        var keyed = new List<(double Key, string GroupKey)>();
        if (rankedKind == "Continuous")
        {
            foreach (var (rv, gk, _) in pairs)
            {
                if (StatisticsVariablePreparer.TryParseNumeric(rv, out double d)) keyed.Add((d, gk));
                else result.DroppedInvalid++;
            }
        }
        else // Ordinal
        {
            if (!TryResolveOrdinalIndex(pairs.Select(p => p.RankedRaw), rankedPrep, out var indexByKey))
            {
                result.Status = RankTestStatus.CannotCompute;
                result.StatusReason = "The ordinal variable's category order could not be determined. Define its order (via coding / value labels or Magic Fix) before running a rank test.";
                AddMethodNotes(result);
                return result;
            }
            foreach (var (rv, gk, _) in pairs)
                keyed.Add((indexByKey[rv.Trim().ToLowerInvariant()], gk));
        }

        result.ValidN = keyed.Count;

        // --- Build groups (deterministic order). -----------------------------
        var groupKeys = groupAcc.OrderedKeys();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, gk) in keyed) counts[gk] = counts.TryGetValue(gk, out int c) ? c + 1 : 1;
        groupKeys = groupKeys.Where(k => counts.ContainsKey(k)).ToList();   // keep only groups with valid pairs

        // --- Guard 2: at least two groups. -----------------------------------
        if (groupKeys.Count < 2 || keyed.Count < 2)
        {
            result.Status = RankTestStatus.CannotCompute;
            result.StatusReason = groupKeys.Count < 2
                ? "The grouping variable has only one observed group after removing missing values, so there is nothing to compare."
                : "There are too few complete observations to compare.";
            AddMethodNotes(result);
            return result;
        }

        // --- Guard 3: every group needs n >= MinGroupSize. -------------------
        var small = groupKeys.Where(k => counts[k] < MinGroupSize).ToList();
        if (small.Count > 0)
        {
            result.Status = RankTestStatus.CannotCompute;
            result.StatusReason =
                $"Each group needs at least {MinGroupSize} observations for the large-sample approximation. " +
                $"Too few in: {string.Join(", ", small.Select(k => $"{groupAcc.DisplayLabel(k)} (n={counts[k]})"))}. " +
                "An exact small-sample test is not available in this version.";
            // Still surface the group sizes so the user sees the shortfall.
            foreach (var k in groupKeys)
                result.Groups.Add(new RankGroupSummary { Label = groupAcc.DisplayLabel(k), N = counts[k] });
            AddMethodNotes(result);
            return result;
        }

        // --- Rank all observations (average ranks for ties). -----------------
        var values = keyed.Select(k => k.Key).ToArray();
        var ranks = AverageRanks(values, out double tieSumT3MinusT);
        int nTotal = values.Length;

        var rankSum = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in groupKeys) rankSum[k] = 0.0;
        for (int i = 0; i < keyed.Count; i++) rankSum[keyed[i].GroupKey] += ranks[i];

        foreach (var k in groupKeys)
            result.Groups.Add(new RankGroupSummary
            {
                Label = groupAcc.DisplayLabel(k),
                N = counts[k],
                RankSum = rankSum[k],
                MeanRank = rankSum[k] / counts[k]
            });

        bool anyTies = tieSumT3MinusT > 0;
        result.TieCorrectionApplied = anyTies;
        result.TieNote = anyTies
            ? "Tied values received average ranks; the variance/statistic was tie-corrected."
            : "No tied values were present, so no tie correction was needed.";

        // --- Choose the test by observed group count. ------------------------
        if (groupKeys.Count == 2)
            ComputeMannWhitney(result, groupKeys, counts, rankSum, groupAcc, nTotal, tieSumT3MinusT);
        else
            ComputeKruskalWallis(result, groupKeys, counts, rankSum, nTotal, tieSumT3MinusT);

        AddAssumptions(result);
        AddMethodNotes(result);
        return result;
    }

    // ---- Mann-Whitney U (2 groups) -------------------------------------------
    private static void ComputeMannWhitney(
        RankTestResult result, List<string> groupKeys, Dictionary<string, int> counts,
        Dictionary<string, double> rankSum, GroupAccumulator groupAcc, int nTotal, double tieSumT3MinusT)
    {
        string g1 = groupKeys[0], g2 = groupKeys[1];
        int n1 = counts[g1], n2 = counts[g2];
        double r1 = rankSum[g1];

        double u1 = r1 - n1 * (n1 + 1) / 2.0;
        double u2 = (double)n1 * n2 - u1;
        double u = Math.Min(u1, u2);
        result.U1 = u1; result.U2 = u2; result.U = u;

        int N = n1 + n2;
        double muU = n1 * (double)n2 / 2.0;
        // Tie-corrected standard deviation of U.
        double tieTerm = (N + 1) - tieSumT3MinusT / ((double)N * (N - 1));
        double varU = (n1 * (double)n2 / 12.0) * tieTerm;

        if (varU <= 0 || N <= 1)
        {
            result.Status = RankTestStatus.CannotCompute;
            result.StatusReason = "There is no variation to compare (all observations are tied), so a Mann-Whitney test cannot be computed.";
            result.TestUsed = "Mann-Whitney U test (not computed)";
            return;
        }

        double sigmaU = Math.Sqrt(varU);
        // Continuity correction toward the mean; never let the numerator go negative.
        double numerator = Math.Max(0.0, Math.Abs(u1 - muU) - 0.5);
        double z = numerator / sigmaU;
        double p = InferenceMath.NormalTwoSidedP(z);

        result.Z = z;
        result.PValue = p;
        result.TestUsed = "Mann-Whitney U test";
        result.Status = RankTestStatus.Computed;

        // Rank-biserial correlation (Kerby): 2·U1/(n1·n2) − 1, in [−1, 1].
        double rrb = 2.0 * u1 / ((double)n1 * n2) - 1.0;
        result.EffectName = "Rank-biserial correlation";
        result.EffectValue = rrb;
        result.EffectDirectionNote =
            $"Positive means “{groupAcc.DisplayLabel(g1)}” tends to rank higher than “{groupAcc.DisplayLabel(g2)}”; magnitude |r| is the effect size.";
    }

    // ---- Kruskal-Wallis (3+ groups) ------------------------------------------
    private static void ComputeKruskalWallis(
        RankTestResult result, List<string> groupKeys, Dictionary<string, int> counts,
        Dictionary<string, double> rankSum, int nTotal, double tieSumT3MinusT)
    {
        int N = nTotal;
        int k = groupKeys.Count;
        double sumRi2OverNi = groupKeys.Sum(g => rankSum[g] * rankSum[g] / counts[g]);
        double h = 12.0 / (N * (double)(N + 1)) * sumRi2OverNi - 3.0 * (N + 1);

        // Tie correction: divide by C = 1 − Σ(t³−t)/(N³−N).
        double denom = (double)N * N * N - N;
        double c = denom > 0 ? 1.0 - tieSumT3MinusT / denom : 1.0;
        if (c <= 0 || N <= 1)
        {
            result.Status = RankTestStatus.CannotCompute;
            result.StatusReason = "There is no variation to compare (all observations are tied), so a Kruskal-Wallis test cannot be computed.";
            result.TestUsed = "Kruskal-Wallis test (not computed)";
            return;
        }
        double hCorrected = h / c;
        int df = k - 1;
        double p = InferenceMath.ChiSquarePValue(hCorrected, df);

        result.H = hCorrected;
        result.DegreesOfFreedom = df;
        result.PValue = p;
        result.TestUsed = "Kruskal-Wallis test";
        result.Status = RankTestStatus.Computed;

        // Epsilon squared = H / (N − 1), in [0, 1].
        double eps2 = N > 1 ? hCorrected / (N - 1) : 0.0;
        result.EffectName = "Epsilon squared";
        result.EffectValue = Math.Clamp(eps2, 0.0, 1.0);
        result.EffectDirectionNote = "Epsilon squared is the proportion of rank variance explained by the grouping (0–1).";
    }

    // ---- Ranking with average ranks for ties --------------------------------
    // Returns the per-observation ranks (1-based) and Σ(t³ − t) over tie groups.
    public static double[] AverageRanks(double[] values, out double tieSumT3MinusT)
    {
        int n = values.Length;
        var idx = Enumerable.Range(0, n).OrderBy(i => values[i]).ToArray();
        var ranks = new double[n];
        tieSumT3MinusT = 0.0;
        int i2 = 0;
        while (i2 < n)
        {
            int j = i2;
            while (j + 1 < n && values[idx[j + 1]] == values[idx[i2]]) j++;
            // tie block idx[i2..j]; ranks are 1-based positions i2+1..j+1
            double avg = (i2 + 1 + j + 1) / 2.0;
            for (int t = i2; t <= j; t++) ranks[idx[t]] = avg;
            int tieLen = j - i2 + 1;
            if (tieLen > 1) tieSumT3MinusT += (double)tieLen * tieLen * tieLen - tieLen;
            i2 = j + 1;
        }
        return ranks;
    }

    // ---- Ordinal order resolution (mirrors the descriptive engine) ----------
    // Precedence: canonical inference (numeric ranges / recognized ordinal
    // scale) → explicit value-label order → all-numeric ascending → unresolved.
    private static bool TryResolveOrdinalIndex(
        IEnumerable<string> rawValues, VariablePreparation prep, out Dictionary<string, int> indexByLowerKey)
    {
        indexByLowerKey = new Dictionary<string, int>(StringComparer.Ordinal);

        // Distinct categories (case-insensitive) with a representative display.
        var groups = rawValues
            .Select(v => v.Trim())
            .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
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
        else if (prep.ValueLabelMap.Count > 0)
        {
            var labelOrder = prep.ValueLabelMap.Keys.ToList();
            int OrderOf(string val)
            {
                for (int i = 0; i < labelOrder.Count; i++)
                    if (string.Equals(labelOrder[i], val, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(prep.ValueLabelMap[labelOrder[i]], val, StringComparison.OrdinalIgnoreCase))
                        return i;
                return int.MaxValue;
            }
            // Every category must be placeable; otherwise the order is unknown.
            if (groups.Any(g => OrderOf(g) == int.MaxValue)) return false;
            ordered = groups.OrderBy(OrderOf).ToList();
        }
        else if (allNumeric)
        {
            ordered = groups.OrderBy(g => { StatisticsVariablePreparer.TryParseNumeric(g, out double d); return d; }).ToList();
        }
        else
        {
            return false;   // ordinal without a resolvable order → refuse
        }

        for (int i = 0; i < ordered.Count; i++)
            indexByLowerKey[ordered[i].Trim().ToLowerInvariant()] = i;

        // Ensure every observed category maps (defensive).
        foreach (var g in groups)
            if (!indexByLowerKey.ContainsKey(g.Trim().ToLowerInvariant())) return false;
        return true;
    }

    private static void AddAssumptions(RankTestResult r)
    {
        r.Assumptions.Add($"Each group has at least {MinGroupSize} observations: met.");
        r.Assumptions.Add("Independent observations: assumed (study-design level; not verifiable from the data).");
        if (r.RankedKind == "Ordinal")
            r.Assumptions.Add("Ordinal category order resolved: yes.");
        if (r.IsRobustAlternative)
            r.Assumptions.Add("This is the robust alternative to the recommended t-test / ANOVA, which is not computed in this version.");
    }

    private static void AddMethodNotes(RankTestResult r)
    {
        r.Notes.Add("Observations were ranked together across groups; tied values received the average of their ranks.");
        r.Notes.Add("Mann-Whitney: U1 = R1 − n1(n1+1)/2, U2 = n1·n2 − U1; z uses the tie-corrected variance with a ±0.5 continuity correction; the two-sided p-value comes from the normal distribution.");
        r.Notes.Add("Kruskal-Wallis: H = 12/(N(N+1)) · Σ(R_g²/n_g) − 3(N+1), tie-corrected by dividing by 1 − Σ(t³−t)/(N³−N); df = groups − 1; p-value from the chi-square upper tail.");
        r.Notes.Add("Effect size — Mann-Whitney: rank-biserial correlation; Kruskal-Wallis: epsilon squared = H/(N−1).");
        r.Notes.Add("Rows missing either variable are excluded pairwise. p-values are never shown as 0; values below .001 are shown as \"< .001\". Full precision is kept internally.");
        r.Notes.Add("No exact small-sample test, post-hoc comparison, confidence interval, or correlation is calculated in this version.");
    }

    private static string ResolveColumn(ResearchVariable v, StatisticsMatchInput match)
        => match.VariableColumn.TryGetValue(v.Id, out string? col) ? col : "";

    // Same display policy as the recommendation engine (label when distinct,
    // else prettify an underscored name; stored name never modified).
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

    // Groups the grouping variable's raw values case-insensitively, tracks the
    // representative spelling, applies value labels for display, and orders
    // groups deterministically (numeric ascending, else count-descending).
    private sealed class GroupAccumulator
    {
        private readonly Dictionary<string, string> _labels;
        private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, int>> _spellings = new(StringComparer.OrdinalIgnoreCase);

        public GroupAccumulator(Dictionary<string, string> labels) => _labels = labels;

        public void Observe(string raw)
        {
            string key = raw.Trim();
            _counts[key] = _counts.TryGetValue(key, out int c) ? c + 1 : 1;
            if (!_spellings.TryGetValue(key, out var sp)) { sp = new(StringComparer.Ordinal); _spellings[key] = sp; }
            sp[raw] = sp.TryGetValue(raw, out int s) ? s + 1 : 1;
        }

        public string DisplayLabel(string key)
        {
            if (_labels.TryGetValue(key, out string? lbl) && !string.IsNullOrWhiteSpace(lbl))
                return $"{key} = {lbl}";
            if (_spellings.TryGetValue(key, out var sp) && sp.Count > 0)
                return sp.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
            return key;
        }

        public List<string> OrderedKeys()
        {
            var keys = _counts.Keys.ToList();
            bool allNumeric = keys.Count > 0 && keys.All(k =>
                double.TryParse(k, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
            if (allNumeric)
                return keys.OrderBy(k => double.Parse(k, CultureInfo.InvariantCulture)).ToList();
            return keys.OrderByDescending(k => _counts[k]).ThenBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}

// ---------------------------------------------------------------------------
// Export: plain text and CSV of a computed rank result. Aggregate only —
// group sizes, rank sums, mean ranks, the statistic, p-value, and effect size.
// No participant-level data; no AI.
// ---------------------------------------------------------------------------
public static class RankInferenceExport
{
    public static string BuildPlainText(RankTestResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RANK-BASED ANALYSIS RESULT");
        sb.AppendLine($"Generated: {r.GeneratedDisplay}");
        sb.AppendLine($"Ranked variable:   {r.RankedDisplay}  ({r.RankedKind})");
        sb.AppendLine($"Grouping variable: {r.GroupingDisplay}");
        sb.AppendLine($"Valid observations: {r.ValidN}   (excluded for missing: {r.DroppedForMissing}; invalid values dropped: {r.DroppedInvalid})");
        sb.AppendLine(new string('=', 78));

        if (r.Status == RankTestStatus.NotRunnable)
        {
            sb.AppendLine("This pairing could not be run.");
            sb.AppendLine(r.StatusReason);
            AppendNotes(sb, r);
            return sb.ToString();
        }

        if (r.Groups.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Groups (aggregate rank summary):");
            foreach (var g in r.Groups)
                sb.AppendLine($"  {g.Label}:  n = {g.N},  rank sum = {InferenceMath.FormatNumber(g.RankSum, 1)},  mean rank = {InferenceMath.FormatNumber(g.MeanRank, 2)}");
        }

        sb.AppendLine();
        if (r.Status == RankTestStatus.CannotCompute)
        {
            sb.AppendLine("RESULT: Cannot compute a reliable result — needs review.");
            sb.AppendLine(r.StatusReason);
        }
        else
        {
            sb.AppendLine($"Test used: {r.TestUsed}");
            if (r.U is not null)
                sb.AppendLine($"U1 = {InferenceMath.FormatNumber(r.U1, 1)}   U2 = {InferenceMath.FormatNumber(r.U2, 1)}   U = {InferenceMath.FormatNumber(r.U, 1)}   z = {InferenceMath.FormatNumber(r.Z, 3)}");
            if (r.H is not null)
                sb.AppendLine($"H = {InferenceMath.FormatNumber(r.H, 3)}   df = {r.DegreesOfFreedom}");
            sb.AppendLine($"p-value: {r.PValueDisplay}");
            sb.AppendLine($"Effect size — {r.EffectName}: {InferenceMath.FormatNumber(r.EffectValue, 3)}");
            if (r.EffectDirectionNote.Length > 0) sb.AppendLine($"  {r.EffectDirectionNote}");
            sb.AppendLine($"Tie correction: {r.TieNote}");
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

    private static void AppendNotes(StringBuilder sb, RankTestResult r)
    {
        sb.AppendLine();
        sb.AppendLine("Notes:");
        foreach (var n in r.Notes) sb.AppendLine("  • " + n);
    }

    public static string BuildCsv(RankTestResult r)
    {
        var sb = new StringBuilder();
        string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

        sb.AppendLine(string.Join(",", new[]
        {
            "Ranked", "RankedKind", "Grouping", "Status", "TestUsed", "ValidN", "MissingExcluded", "InvalidDropped",
            "U1", "U2", "U", "z", "H", "df", "p", "EffectName", "EffectValue", "TieCorrected", "RobustAlternative", "AiInvolved"
        }.Select(Q)));

        sb.AppendLine(string.Join(",", new[]
        {
            Q(r.RankedDisplay), Q(r.RankedKind), Q(r.GroupingDisplay), Q(r.Status.ToString()), Q(r.TestUsed),
            r.ValidN.ToString(CultureInfo.InvariantCulture), r.DroppedForMissing.ToString(CultureInfo.InvariantCulture),
            r.DroppedInvalid.ToString(CultureInfo.InvariantCulture),
            Q(InferenceMath.FormatNumber(r.U1, 2)), Q(InferenceMath.FormatNumber(r.U2, 2)), Q(InferenceMath.FormatNumber(r.U, 2)),
            Q(InferenceMath.FormatNumber(r.Z, 4)), Q(InferenceMath.FormatNumber(r.H, 4)),
            r.DegreesOfFreedom.ToString(CultureInfo.InvariantCulture),
            Q(r.PValue is null ? "not calculated" : InferenceMath.FormatPValue(r.PValue.Value)),
            Q(r.EffectName), Q(InferenceMath.FormatNumber(r.EffectValue, 4)),
            Q(r.TieCorrectionApplied ? "yes" : "no"), Q(r.IsRobustAlternative ? "yes" : "no"), Q("no")
        }));

        sb.AppendLine();
        sb.AppendLine(string.Join(",", new[] { "Group", "N", "RankSum", "MeanRank" }.Select(Q)));
        foreach (var g in r.Groups)
            sb.AppendLine(string.Join(",", new[]
            {
                Q(g.Label), g.N.ToString(CultureInfo.InvariantCulture),
                Q(InferenceMath.FormatNumber(g.RankSum, 2)), Q(InferenceMath.FormatNumber(g.MeanRank, 3))
            }));
        return sb.ToString();
    }
}
