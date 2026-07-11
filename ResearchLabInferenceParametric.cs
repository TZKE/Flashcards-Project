using System.Globalization;
using System.Text;

namespace AIFlashcardMaker;

// ===========================================================================
// Research Lab — Phase 4D: deterministic PARAMETRIC inference.
//
//   * Slice 1 — Welch independent-samples t-test: a continuous outcome compared
//     between two independent groups (a binary / two-level grouping variable).
//   * Slice 2 — one-way ANOVA: a continuous outcome compared across a categorical
//     grouping variable with 3+ independent groups.
//
// Builds on the earlier phases and shares InferenceMath.
//
// HARD RULES (audit-critical):
//   * Deterministic C# only (System.Math). Same inputs → bit-identical output.
//   * NO randomness. The Welch p-value is the Student-t tail with the Welch–
//     Satterthwaite (fractional) degrees of freedom; the ANOVA p-value is the
//     right tail of the F distribution — both computed from the already-validated
//     InferenceMath incomplete-beta core (no new special functions here).
//   * NO AI, HTTP, network, logging, or file I/O. Consumes already-loaded,
//     in-memory data; raw participant rows never leave this device or reach a
//     log / AI. Every result and export is aggregate-only.
//   * WELCH t-test + one-way ANOVA ONLY. NO Student equal-variance t-test, NO
//     paired t-test, NO two-way / repeated-measures ANOVA, NO ANCOVA, NO post-hoc
//     (Tukey) tests, NO Pearson, NO odds ratio / risk ratio / confidence interval,
//     NO regression. This engine never chooses a test — it executes ONLY the
//     recommendation that Part 1 already produced for an eligible pairing.
//   * No WPF dependency — everything here is headless-testable.
// ===========================================================================

public enum ParametricStatus
{
    Computed,       // a valid t / df / p were produced
    CannotCompute,  // inputs valid-shaped but a guardrail blocks a reliable result
    NotRunnable     // the pairing is not eligible for a Welch t-test
}

// One group's aggregate summary (never any participant rows).
public sealed class WelchGroupSummary
{
    public string Label { get; set; } = "";
    public int N { get; set; }
    public double Mean { get; set; }
    public double Sd { get; set; }
}

public sealed class WelchTTestResult : IInferenceExportable
{
    public string OutcomeName { get; set; } = "";
    public string OutcomeDisplay { get; set; } = "";
    public string OutcomeKind { get; set; } = "";        // "Continuous"
    public string GroupingName { get; set; } = "";
    public string GroupingDisplay { get; set; } = "";
    public string GroupingKind { get; set; } = "";       // "Binary"
    public string PairTypeDisplay { get; set; } = "";

    public ParametricStatus Status { get; set; } = ParametricStatus.NotRunnable;
    public string StatusReason { get; set; } = "";
    public string TestUsed { get; set; } = "Welch independent-samples t-test";

    public int ValidN { get; set; }
    public int DroppedForMissing { get; set; }
    public int DroppedInvalid { get; set; }

    public List<WelchGroupSummary> Groups { get; set; } = new();

    public double? MeanDifference { get; set; }
    public double? TStatistic { get; set; }
    public double? DegreesOfFreedom { get; set; }        // Welch–Satterthwaite (fractional)
    public double? PValue { get; set; }

    public double? CohensD { get; set; }                 // pooled-SD standardized mean difference
    public double? HedgesG { get; set; }                 // small-sample-corrected (primary effect size)
    public string EffectDirectionNote { get; set; } = "";

    public List<string> Assumptions { get; set; } = new();
    public List<string> Notes { get; set; } = new();

    public bool AiInvolved => false;
    public bool Computed => Status == ParametricStatus.Computed;
    public string PValueDisplay => PValue is null ? "not calculated" : InferenceMath.FormatPValue(PValue.Value);
    public string GeneratedDisplay => DateTime.UtcNow.ToLocalTime().ToString("MMM d, yyyy · h:mm tt", CultureInfo.InvariantCulture);

    // Hedges g is the headline effect size (less biased than Cohen's d for small n).
    public string EffectName => "Hedges g";
    public double? EffectValue => HedgesG;

    // Heuristic strength band — labelled a heuristic, never a hard verdict.
    public string StrengthBand
    {
        get
        {
            if (HedgesG is not { } g) return "";
            double a = Math.Abs(g);
            string s = a < 0.20 ? "negligible" : a < 0.50 ? "small" : a < 0.80 ? "medium" : "large";
            return $"{s} standardized difference (heuristic)";
        }
    }

    // IInferenceExportable.
    public string ResultTitle => $"{OutcomeDisplay}  by  {GroupingDisplay}";
    public string ToPlainText() => WelchTTestExport.BuildPlainText(this);
    public string ToCsv() => WelchTTestExport.BuildCsv(this);
}

// One group's aggregate summary for ANOVA (never any participant rows).
public sealed class AnovaGroupSummary
{
    public string Label { get; set; } = "";
    public int N { get; set; }
    public double Mean { get; set; }
    public double Sd { get; set; }
}

public sealed class OneWayAnovaResult : IInferenceExportable
{
    public string OutcomeName { get; set; } = "";
    public string OutcomeDisplay { get; set; } = "";
    public string OutcomeKind { get; set; } = "";        // "Continuous"
    public string GroupingName { get; set; } = "";
    public string GroupingDisplay { get; set; } = "";
    public string GroupingKind { get; set; } = "";       // "Categorical"
    public string PairTypeDisplay { get; set; } = "";

    public ParametricStatus Status { get; set; } = ParametricStatus.NotRunnable;
    public string StatusReason { get; set; } = "";
    public string TestUsed { get; set; } = "One-way ANOVA";

    public int ValidN { get; set; }
    public int GroupCount { get; set; }
    public int DroppedForMissing { get; set; }
    public int DroppedInvalid { get; set; }

    public List<AnovaGroupSummary> Groups { get; set; } = new();

    public double? GrandMean { get; set; }
    public double? SsBetween { get; set; }
    public double? SsWithin { get; set; }
    public double? SsTotal { get; set; }
    public int DfBetween { get; set; }
    public int DfWithin { get; set; }
    public double? MsBetween { get; set; }
    public double? MsWithin { get; set; }
    public double? FStatistic { get; set; }
    public double? PValue { get; set; }                  // right-tail F p-value

    public double? EtaSquared { get; set; }              // SS_between / SS_total (headline)
    public double? OmegaSquared { get; set; }            // small-sample corrected, clamped ≥ 0

    public List<string> Assumptions { get; set; } = new();
    public List<string> Notes { get; set; } = new();

    public bool AiInvolved => false;
    public bool Computed => Status == ParametricStatus.Computed;
    public string PValueDisplay => PValue is null ? "not calculated" : InferenceMath.FormatPValue(PValue.Value);
    public string GeneratedDisplay => DateTime.UtcNow.ToLocalTime().ToString("MMM d, yyyy · h:mm tt", CultureInfo.InvariantCulture);

    // eta-squared is the headline effect size (share of variance explained).
    public string EffectName => "eta-squared";
    public double? EffectValue => EtaSquared;

    // Heuristic strength band — labelled a heuristic, never a hard verdict.
    // Cohen's eta-squared benchmarks: .01 small, .06 medium, .14 large.
    public string StrengthBand
    {
        get
        {
            if (EtaSquared is not { } e) return "";
            string s = e < 0.01 ? "negligible" : e < 0.06 ? "small" : e < 0.14 ? "medium" : "large";
            return $"{s} share of variance explained (heuristic)";
        }
    }

    // IInferenceExportable.
    public string ResultTitle => $"{OutcomeDisplay}  by  {GroupingDisplay}";
    public string ToPlainText() => OneWayAnovaExport.BuildPlainText(this);
    public string ToCsv() => OneWayAnovaExport.BuildCsv(this);
}

// ---------------------------------------------------------------------------
// The deterministic parametric-inference engine (Welch t-test + one-way ANOVA).
// ---------------------------------------------------------------------------
public static class ParametricInferenceEngine
{
    public const int MinGroupsForAnova = 3;   // fewer than 3 groups is a t-test, not ANOVA
    public const int MinGroupN = 2;   // sample variance needs n − 1 ≥ 1

    public static bool IsRunnable(TestRecommendation? rec) => rec is not null && rec.CanComputeWelch;

    private static bool IsContinuousKind(string k) => k == "Continuous";
    private static bool IsBinaryKind(string k) => k == "Binary";

    public static WelchTTestResult ComputeWelchTTest(
        TestRecommendation rec,
        ResearchVariable? outcome,
        ResearchVariable? predictor,
        StatisticsDataset? data,
        StatisticsMatchInput? match)
    {
        var result = new WelchTTestResult { PairTypeDisplay = rec?.PairTypeDisplay ?? "" };
        result.Notes.Add("Computed locally on this device by deterministic C# code. No AI was used to select or calculate this test.");
        result.Notes.Add("Only aggregate group summaries are shown or exported — no individual participant rows.");

        // --- Guard 1: eligibility. ------------------------------------------
        if (!IsRunnable(rec))
        {
            result.Status = ParametricStatus.NotRunnable;
            result.StatusReason = "This pairing is not eligible for a Welch t-test. Only a continuous outcome compared across a binary (two-level) grouping variable that is ready to plan or needs assumption review can be computed.";
            return result;
        }
        if (outcome is null || predictor is null || data is null || data.RowCount == 0 || match is null)
        {
            result.Status = ParametricStatus.NotRunnable;
            result.StatusReason = "The dataset or matched variables were not available to compute this test.";
            return result;
        }

        // The eligibility gate guarantees outcome = Continuous, predictor = Binary.
        // Re-check defensively so the engine never guesses if called incorrectly.
        if (!IsContinuousKind(rec!.OutcomeKind) || !IsBinaryKind(rec.PredictorKind))
        {
            result.Status = ParametricStatus.NotRunnable;
            result.StatusReason = "A Welch t-test needs a continuous outcome and a binary grouping variable.";
            return result;
        }

        ResearchVariable measured = outcome, grouping = predictor;
        result.OutcomeName = measured.VariableName.Trim();
        result.OutcomeDisplay = Display(measured);
        result.OutcomeKind = "Continuous";
        result.GroupingName = grouping.VariableName.Trim();
        result.GroupingDisplay = Display(grouping);
        result.GroupingKind = "Binary";

        var measuredPrep = StatisticsVariablePreparer.Prepare(measured, data, ResolveColumn(measured, match));
        var groupPrep = StatisticsVariablePreparer.Prepare(grouping, data, ResolveColumn(grouping, match));
        int mIdx = data.ColumnIndexOf(measuredPrep.MatchedColumn);
        int gIdx = data.ColumnIndexOf(groupPrep.MatchedColumn);
        if (mIdx < 0 || gIdx < 0)
        {
            result.Status = ParametricStatus.NotRunnable;
            result.StatusReason = "The matched dataset columns for these variables could not be found.";
            return result;
        }

        // --- Assemble aligned (numericValue, groupKey) pairs (listwise). -----
        var valuesByGroup = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        var groupAcc = new GroupAccumulator(StatisticsVariablePreparer.ParseValueLabels(grouping.ValueLabels, grouping.Coding));
        for (int r = 0; r < data.RowCount; r++)
        {
            string mv = data.Cell(r, mIdx).Trim();
            string gv = data.Cell(r, gIdx).Trim();
            if (StatisticsMissingTokens.IsMissing(mv) || StatisticsMissingTokens.IsMissing(gv))
            {
                result.DroppedForMissing++;
                continue;
            }
            if (!StatisticsVariablePreparer.TryParseNumeric(mv, out double d))
            {
                result.DroppedInvalid++;   // grouping present but the outcome value is non-numeric
                continue;
            }
            string key = gv.Trim();
            groupAcc.Observe(gv);
            if (!valuesByGroup.TryGetValue(key, out var list)) { list = new List<double>(); valuesByGroup[key] = list; }
            list.Add(d);
        }
        result.ValidN = valuesByGroup.Values.Sum(v => v.Count);

        var groupKeys = groupAcc.OrderedKeys().Where(k => valuesByGroup.ContainsKey(k)).ToList();

        // --- Guard 2: exactly two groups. -----------------------------------
        if (groupKeys.Count < 2)
        {
            result.Status = ParametricStatus.CannotCompute;
            result.StatusReason = "The grouping variable has only one observed group after removing missing values, so there is nothing to compare.";
            AddMethodNotes(result);
            return result;
        }
        if (groupKeys.Count > 2)
        {
            result.Status = ParametricStatus.CannotCompute;
            result.StatusReason = $"The grouping variable has {groupKeys.Count} observed groups. A Welch t-test compares exactly two groups — for three or more groups a different test would be needed (not available in this version).";
            AddMethodNotes(result);
            return result;
        }

        string k1 = groupKeys[0], k2 = groupKeys[1];
        var g1 = valuesByGroup[k1];
        var g2 = valuesByGroup[k2];
        int n1 = g1.Count, n2 = g2.Count;

        // --- Guard 3: each group needs n >= 2 (for a sample variance). ------
        if (n1 < MinGroupN || n2 < MinGroupN)
        {
            result.Status = ParametricStatus.CannotCompute;
            result.StatusReason = $"Each group needs at least {MinGroupN} valid observations to estimate a variance. Observed: {groupAcc.DisplayLabel(k1)} (n={n1}), {groupAcc.DisplayLabel(k2)} (n={n2}).";
            result.Groups.Add(new WelchGroupSummary { Label = groupAcc.DisplayLabel(k1), N = n1 });
            result.Groups.Add(new WelchGroupSummary { Label = groupAcc.DisplayLabel(k2), N = n2 });
            AddMethodNotes(result);
            return result;
        }

        double mean1 = g1.Average(), mean2 = g2.Average();
        double var1 = SampleVariance(g1, mean1), var2 = SampleVariance(g2, mean2);
        double sd1 = Math.Sqrt(var1), sd2 = Math.Sqrt(var2);

        result.Groups.Add(new WelchGroupSummary { Label = groupAcc.DisplayLabel(k1), N = n1, Mean = mean1, Sd = sd1 });
        result.Groups.Add(new WelchGroupSummary { Label = groupAcc.DisplayLabel(k2), N = n2, Mean = mean2, Sd = sd2 });

        // --- Guard 4: both groups constant → no variance to test. -----------
        double s1n = var1 / n1, s2n = var2 / n2;
        double se = Math.Sqrt(s1n + s2n);
        if (se <= 0.0 || double.IsNaN(se))
        {
            result.Status = ParametricStatus.CannotCompute;
            result.StatusReason = "Both groups have zero variance (every value is identical), so a t-test cannot be computed.";
            AddMethodNotes(result);
            return result;
        }

        // --- Welch t statistic + Welch–Satterthwaite df. --------------------
        double meanDiff = mean1 - mean2;
        double t = meanDiff / se;
        double dfNum = (s1n + s2n) * (s1n + s2n);
        double dfDen = (s1n * s1n) / (n1 - 1) + (s2n * s2n) / (n2 - 1);
        double df = dfDen > 0 ? dfNum / dfDen : double.NaN;
        if (double.IsNaN(df) || double.IsInfinity(df) || df < 1.0 || double.IsNaN(t) || double.IsInfinity(t))
        {
            result.Status = ParametricStatus.CannotCompute;
            result.StatusReason = "The Welch degrees of freedom or test statistic could not be computed from these groups.";
            AddMethodNotes(result);
            return result;
        }

        result.MeanDifference = meanDiff;
        result.TStatistic = t;
        result.DegreesOfFreedom = df;
        result.PValue = InferenceMath.StudentTTwoSidedP(t, df);

        // --- Effect size: pooled-SD Cohen's d + small-sample Hedges g. ------
        double pooledVar = ((n1 - 1) * var1 + (n2 - 1) * var2) / (n1 + n2 - 2);
        double pooledSd = Math.Sqrt(pooledVar);
        if (pooledSd > 0)
        {
            double d = meanDiff / pooledSd;
            double j = 1.0 - 3.0 / (4.0 * (n1 + n2) - 9.0);   // Hedges small-sample correction
            result.CohensD = d;
            result.HedgesG = d * j;
        }
        result.EffectDirectionNote =
            $"Mean difference is “{groupAcc.DisplayLabel(k1)}” minus “{groupAcc.DisplayLabel(k2)}”; a positive value means the first group has the higher mean.";

        result.Status = ParametricStatus.Computed;
        AddAssumptions(result);
        AddMethodNotes(result);
        return result;
    }

    // =====================================================================
    // One-way ANOVA (Slice 2): continuous outcome × categorical predictor
    // with 3+ independent groups. Shares the same GroupAccumulator, display,
    // and sample-variance helpers as the Welch path above.
    // =====================================================================
    public static bool IsRunnableAnova(TestRecommendation? rec) => rec is not null && rec.CanComputeAnova;

    private static bool IsCategoricalKind(string k) => k == "Categorical";

    public static OneWayAnovaResult ComputeOneWayAnova(
        TestRecommendation rec,
        ResearchVariable? outcome,
        ResearchVariable? predictor,
        StatisticsDataset? data,
        StatisticsMatchInput? match)
    {
        var result = new OneWayAnovaResult { PairTypeDisplay = rec?.PairTypeDisplay ?? "" };
        result.Notes.Add("Computed locally on this device by deterministic C# code. No AI was used to select or calculate this test.");
        result.Notes.Add("Only aggregate group summaries are shown or exported — no individual participant rows.");

        // --- Guard 1: eligibility. ------------------------------------------
        if (!IsRunnableAnova(rec))
        {
            result.Status = ParametricStatus.NotRunnable;
            result.StatusReason = "This pairing is not eligible for a one-way ANOVA. Only a continuous outcome compared across a categorical grouping variable with 3+ groups that is ready to plan or needs assumption review can be computed.";
            return result;
        }
        if (outcome is null || predictor is null || data is null || data.RowCount == 0 || match is null)
        {
            result.Status = ParametricStatus.NotRunnable;
            result.StatusReason = "The dataset or matched variables were not available to compute this test.";
            return result;
        }

        // The eligibility gate guarantees outcome = Continuous, predictor =
        // Categorical (3+ groups). Re-check defensively so the engine never
        // guesses if called incorrectly.
        if (!IsContinuousKind(rec!.OutcomeKind) || !IsCategoricalKind(rec.PredictorKind))
        {
            result.Status = ParametricStatus.NotRunnable;
            result.StatusReason = "A one-way ANOVA needs a continuous outcome and a categorical grouping variable.";
            return result;
        }

        ResearchVariable measured = outcome, grouping = predictor;
        result.OutcomeName = measured.VariableName.Trim();
        result.OutcomeDisplay = Display(measured);
        result.OutcomeKind = "Continuous";
        result.GroupingName = grouping.VariableName.Trim();
        result.GroupingDisplay = Display(grouping);
        result.GroupingKind = "Categorical";

        var measuredPrep = StatisticsVariablePreparer.Prepare(measured, data, ResolveColumn(measured, match));
        var groupPrep = StatisticsVariablePreparer.Prepare(grouping, data, ResolveColumn(grouping, match));
        int mIdx = data.ColumnIndexOf(measuredPrep.MatchedColumn);
        int gIdx = data.ColumnIndexOf(groupPrep.MatchedColumn);
        if (mIdx < 0 || gIdx < 0)
        {
            result.Status = ParametricStatus.NotRunnable;
            result.StatusReason = "The matched dataset columns for these variables could not be found.";
            return result;
        }

        // --- Assemble aligned (numericValue, groupKey) pairs (listwise). -----
        var valuesByGroup = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        var groupAcc = new GroupAccumulator(StatisticsVariablePreparer.ParseValueLabels(grouping.ValueLabels, grouping.Coding));
        for (int r = 0; r < data.RowCount; r++)
        {
            string mv = data.Cell(r, mIdx).Trim();
            string gv = data.Cell(r, gIdx).Trim();
            if (StatisticsMissingTokens.IsMissing(mv) || StatisticsMissingTokens.IsMissing(gv))
            {
                result.DroppedForMissing++;
                continue;
            }
            if (!StatisticsVariablePreparer.TryParseNumeric(mv, out double d))
            {
                result.DroppedInvalid++;   // grouping present but the outcome value is non-numeric
                continue;
            }
            string key = gv.Trim();
            groupAcc.Observe(gv);
            if (!valuesByGroup.TryGetValue(key, out var list)) { list = new List<double>(); valuesByGroup[key] = list; }
            list.Add(d);
        }
        result.ValidN = valuesByGroup.Values.Sum(v => v.Count);

        // Deterministic group order (numeric-asc when coded numerically, else
        // count-desc with an ordinal tie-break) — never silently merges groups.
        var groupKeys = groupAcc.OrderedKeys().Where(k => valuesByGroup.ContainsKey(k)).ToList();
        result.GroupCount = groupKeys.Count;

        // --- Guard 2: at least three groups. --------------------------------
        if (groupKeys.Count < MinGroupsForAnova)
        {
            result.Status = ParametricStatus.CannotCompute;
            result.StatusReason = groupKeys.Count == 2
                ? "Only two groups were observed after removing missing values. A two-group comparison uses a t-test, not a one-way ANOVA."
                : $"A one-way ANOVA needs at least {MinGroupsForAnova} groups; only {groupKeys.Count} observed group(s) remain after removing missing values.";
            foreach (var k in groupKeys)
                result.Groups.Add(new AnovaGroupSummary { Label = groupAcc.DisplayLabel(k), N = valuesByGroup[k].Count });
            AddMethodNotesAnova(result);
            return result;
        }

        // --- Group means + grand mean (needed by every remaining branch). ---
        var means = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        double grandSum = 0.0; int grandN = 0;
        foreach (var k in groupKeys)
        {
            var xs = valuesByGroup[k];
            means[k] = xs.Average();
            grandSum += xs.Sum();
            grandN += xs.Count;
        }
        double grandMean = grandN > 0 ? grandSum / grandN : double.NaN;
        foreach (var k in groupKeys)
        {
            var xs = valuesByGroup[k];
            double sd = Math.Sqrt(SampleVariance(xs, means[k]));
            result.Groups.Add(new AnovaGroupSummary { Label = groupAcc.DisplayLabel(k), N = xs.Count, Mean = means[k], Sd = sd });
        }

        // --- Guard 3: every group needs n >= 2 for a within-group variance. -
        var smallGroups = groupKeys.Where(k => valuesByGroup[k].Count < MinGroupN).ToList();
        if (smallGroups.Count > 0)
        {
            result.Status = ParametricStatus.CannotCompute;
            string names = string.Join(", ", smallGroups.Select(k => $"{groupAcc.DisplayLabel(k)} (n={valuesByGroup[k].Count})"));
            result.StatusReason = $"Each group needs at least {MinGroupN} valid observations to estimate a variance. Too few in: {names}.";
            AddMethodNotesAnova(result);
            return result;
        }

        // --- Sums of squares. -----------------------------------------------
        int k_ = groupKeys.Count;
        int N = grandN;
        double ssBetween = 0.0, ssWithin = 0.0;
        foreach (var k in groupKeys)
        {
            var xs = valuesByGroup[k];
            double diffB = means[k] - grandMean;
            ssBetween += xs.Count * diffB * diffB;
            foreach (double x in xs) { double dv = x - means[k]; ssWithin += dv * dv; }
        }
        double ssTotal = ssBetween + ssWithin;
        int dfBetween = k_ - 1;
        int dfWithin = N - k_;

        result.GrandMean = grandMean;
        result.SsBetween = ssBetween;
        result.SsWithin = ssWithin;
        result.SsTotal = ssTotal;
        result.DfBetween = dfBetween;
        result.DfWithin = dfWithin;

        // --- Guard 4: total variability must be positive. -------------------
        if (ssTotal <= 0.0 || double.IsNaN(ssTotal))
        {
            result.Status = ParametricStatus.CannotCompute;
            result.StatusReason = "The outcome has no variability at all (every value is identical), so a one-way ANOVA cannot be computed.";
            AddMethodNotesAnova(result);
            return result;
        }

        // --- Guard 5: within-group variability must be positive to form F. --
        if (dfWithin < 1 || ssWithin <= 0.0 || double.IsNaN(ssWithin))
        {
            result.Status = ParametricStatus.CannotCompute;
            result.StatusReason = ssWithin <= 0.0
                ? "Every value within each group is identical (zero within-group variance), so a reliable F statistic cannot be formed. The group means and between-group variation are shown, but no p-value is reported."
                : "There are too few residual degrees of freedom to compute a within-group variance.";
            AddMethodNotesAnova(result);
            return result;
        }

        double msBetween = ssBetween / dfBetween;
        double msWithin = ssWithin / dfWithin;
        result.MsBetween = msBetween;
        result.MsWithin = msWithin;

        if (msWithin <= 0.0 || double.IsNaN(msWithin))
        {
            result.Status = ParametricStatus.CannotCompute;
            result.StatusReason = "The within-group mean square is zero, so an F statistic cannot be formed.";
            AddMethodNotesAnova(result);
            return result;
        }

        double f = msBetween / msWithin;
        if (double.IsNaN(f) || double.IsInfinity(f) || f < 0.0)
        {
            result.Status = ParametricStatus.CannotCompute;
            result.StatusReason = "The F statistic could not be computed from these groups.";
            AddMethodNotesAnova(result);
            return result;
        }

        result.FStatistic = f;
        result.PValue = InferenceMath.FDistributionRightTailP(f, dfBetween, dfWithin);

        // --- Effect sizes: eta-squared (headline) + omega-squared. ----------
        result.EtaSquared = ssTotal > 0 ? ssBetween / ssTotal : (double?)null;
        double omega = (ssBetween - dfBetween * msWithin) / (ssTotal + msWithin);
        if (double.IsNaN(omega) || double.IsInfinity(omega)) omega = 0.0;
        result.OmegaSquared = Math.Max(0.0, omega);   // clamp a negative sampling artefact to 0

        result.Status = ParametricStatus.Computed;
        AddAssumptionsAnova(result);
        AddMethodNotesAnova(result);
        return result;
    }

    private static void AddAssumptionsAnova(OneWayAnovaResult r)
    {
        r.Assumptions.Add("Independent observations: assumed (study-design level; not verifiable from the data).");
        r.Assumptions.Add("Continuous outcome measured across 3+ independent groups.");
        r.Assumptions.Add("Approximate normality within each group is assumed; this is not verified automatically in this version.");
        r.Assumptions.Add("Homogeneity of variance (similar spread across groups) is assumed by the standard one-way ANOVA; this is not automatically verified in this version.");
        r.Assumptions.Add("ANOVA is sensitive to outliers. If the data are skewed or the variances differ, the Kruskal-Wallis test may be considered as a robust nonparametric alternative.");
        r.Assumptions.Add("A significant ANOVA indicates that at least one group mean differs; it does NOT identify which groups differ. Post-hoc pairwise comparisons (not available in this version) are needed for that.");
    }

    private static void AddMethodNotesAnova(OneWayAnovaResult r)
    {
        r.Notes.Add("Group means and sample standard deviations use n − 1 (sample variance). Between-groups SS = Σ nᵢ(meanᵢ − grand mean)²; within-groups SS = Σ Σ (x − meanᵢ)²; total SS = between + within.");
        r.Notes.Add("df between = k − 1; df within = N − k. Mean square = SS ÷ df. F = MS between ÷ MS within. The p-value is the right tail of the F distribution (from the regularized incomplete beta).");
        r.Notes.Add("Effect size: eta-squared η² = SS between ÷ SS total (headline). Omega-squared ω² applies a small-sample correction and is clamped at 0 when a sampling artefact makes it negative. Strength labels (negligible/small/medium/large) are a rough heuristic, not a definitive judgement.");
        r.Notes.Add("Rows missing either variable, or with a non-numeric outcome value, are excluded. p-values are never shown as 0; values below .001 are shown as \"< .001\". Full precision is kept internally.");
        r.Notes.Add("One-way ANOVA only. No two-way / repeated-measures ANOVA, ANCOVA, post-hoc (Tukey) tests, equal/unequal-variance t-test, Pearson, odds ratio, confidence interval, or regression is calculated in this version.");
    }

    private static double SampleVariance(List<double> xs, double mean)
    {
        int n = xs.Count;
        if (n < 2) return 0.0;
        double ss = 0.0;
        foreach (double x in xs) { double dv = x - mean; ss += dv * dv; }
        return ss / (n - 1);
    }

    private static void AddAssumptions(WelchTTestResult r)
    {
        r.Assumptions.Add("Independent observations: assumed (study-design level; not verifiable from the data).");
        r.Assumptions.Add("Continuous outcome measured on two independent groups.");
        r.Assumptions.Add("Welch t-test does NOT assume equal variances (this is why Welch is used rather than the equal-variance Student t-test).");
        r.Assumptions.Add("Approximate normality within each group is assumed; this is not verified automatically in this version.");
        r.Assumptions.Add("The t-test is sensitive to outliers. If the data are skewed or have outliers, the Mann-Whitney U test may be considered as a robust nonparametric alternative.");
    }

    private static void AddMethodNotes(WelchTTestResult r)
    {
        r.Notes.Add("Group means and sample standard deviations use n − 1 (sample variance). Standard error = √(s₁²/n₁ + s₂²/n₂).");
        r.Notes.Add("t = (mean₁ − mean₂) / SE. Degrees of freedom use the Welch–Satterthwaite approximation (fractional). Two-sided p-value from the Student-t distribution.");
        r.Notes.Add("Effect size: Cohen's d uses the pooled SD (a descriptive standardized mean difference; the pooled SD assumes equal variances even though the Welch test itself does not). Hedges g applies the small-sample correction and is reported as the headline effect size.");
        r.Notes.Add("Effect-size strength labels (negligible/small/medium/large) are a rough heuristic, not a definitive judgement.");
        r.Notes.Add("Rows missing either variable, or with a non-numeric outcome value, are excluded. p-values are never shown as 0; values below .001 are shown as \"< .001\". Full precision is kept internally.");
        r.Notes.Add("Welch t-test only. No equal-variance t-test, paired t-test, ANOVA, correlation, odds ratio, confidence interval, or regression is calculated in this version.");
    }

    private static string ResolveColumn(ResearchVariable v, StatisticsMatchInput match)
        => match.VariableColumn.TryGetValue(v.Id, out string? col) ? col : "";

    // Same display policy as the other engines (label when distinct, else
    // prettify an underscored name; stored name never modified).
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
    // groups deterministically (numeric ascending, else count-descending) — the
    // SAME convention as the rank engine, so a card that could run either test
    // labels its two groups identically. Duplicated here to keep the committed
    // rank engine byte-for-byte untouched.
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
// Export: plain text and CSV of a computed Welch result. Aggregate only —
// group sizes, means, SDs, the statistic, df, p-value, and effect size.
// No participant-level data; no AI.
// ---------------------------------------------------------------------------
public static class WelchTTestExport
{
    public static string BuildPlainText(WelchTTestResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WELCH T-TEST RESULT");
        sb.AppendLine($"Generated: {r.GeneratedDisplay}");
        sb.AppendLine($"Outcome:   {r.OutcomeDisplay}  (Continuous)");
        sb.AppendLine($"Grouping:  {r.GroupingDisplay}  (Binary)");
        sb.AppendLine($"Complete valid observations (N): {r.ValidN}   (excluded for missing: {r.DroppedForMissing}; non-numeric outcome dropped: {r.DroppedInvalid})");
        sb.AppendLine(new string('=', 78));

        if (r.Status == ParametricStatus.NotRunnable)
        {
            sb.AppendLine("This pairing could not be run.");
            sb.AppendLine(r.StatusReason);
            AppendNotes(sb, r);
            return sb.ToString();
        }

        if (r.Groups.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Groups (aggregate summary):");
            foreach (var g in r.Groups)
                sb.AppendLine($"  {g.Label}:  n = {g.N},  mean = {InferenceMath.FormatNumber(g.Mean, 3)},  SD = {InferenceMath.FormatNumber(g.Sd, 3)}");
        }

        sb.AppendLine();
        if (r.Status == ParametricStatus.CannotCompute)
        {
            sb.AppendLine("RESULT: Cannot compute a reliable result — needs review.");
            sb.AppendLine(r.StatusReason);
        }
        else
        {
            sb.AppendLine($"Test used: {r.TestUsed}");
            sb.AppendLine($"Mean difference: {InferenceMath.FormatNumber(r.MeanDifference, 3)}");
            sb.AppendLine($"t = {InferenceMath.FormatNumber(r.TStatistic, 3)}   Welch df = {InferenceMath.FormatNumber(r.DegreesOfFreedom, 2)}");
            sb.AppendLine($"p-value: {r.PValueDisplay}");
            sb.AppendLine($"Effect size — Hedges g: {InferenceMath.FormatNumber(r.HedgesG, 3)}   (Cohen's d: {InferenceMath.FormatNumber(r.CohensD, 3)})");
            sb.AppendLine($"Strength: {r.StrengthBand}");
            if (r.EffectDirectionNote.Length > 0) sb.AppendLine($"  {r.EffectDirectionNote}");
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

    private static void AppendNotes(StringBuilder sb, WelchTTestResult r)
    {
        sb.AppendLine();
        sb.AppendLine("Notes:");
        foreach (var n in r.Notes) sb.AppendLine("  • " + n);
    }

    public static string BuildCsv(WelchTTestResult r)
    {
        var sb = new StringBuilder();
        string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

        string g1Label = r.Groups.Count > 0 ? r.Groups[0].Label : "";
        string g2Label = r.Groups.Count > 1 ? r.Groups[1].Label : "";
        int g1N = r.Groups.Count > 0 ? r.Groups[0].N : 0;
        int g2N = r.Groups.Count > 1 ? r.Groups[1].N : 0;
        double? g1Mean = r.Groups.Count > 0 ? r.Groups[0].Mean : null;
        double? g2Mean = r.Groups.Count > 1 ? r.Groups[1].Mean : null;
        double? g1Sd = r.Groups.Count > 0 ? r.Groups[0].Sd : null;
        double? g2Sd = r.Groups.Count > 1 ? r.Groups[1].Sd : null;

        sb.AppendLine(string.Join(",", new[]
        {
            "Test", "Outcome", "Grouping", "Status", "ValidN", "MissingExcluded", "InvalidDropped",
            "Group1", "n1", "mean1", "sd1", "Group2", "n2", "mean2", "sd2",
            "MeanDifference", "t", "df", "p", "CohensD", "HedgesG", "AiInvolved"
        }.Select(Q)));

        sb.AppendLine(string.Join(",", new[]
        {
            Q(r.TestUsed), Q(r.OutcomeDisplay), Q(r.GroupingDisplay), Q(r.Status.ToString()),
            r.ValidN.ToString(CultureInfo.InvariantCulture), r.DroppedForMissing.ToString(CultureInfo.InvariantCulture),
            r.DroppedInvalid.ToString(CultureInfo.InvariantCulture),
            Q(g1Label), g1N.ToString(CultureInfo.InvariantCulture), Q(InferenceMath.FormatNumber(g1Mean, 4)), Q(InferenceMath.FormatNumber(g1Sd, 4)),
            Q(g2Label), g2N.ToString(CultureInfo.InvariantCulture), Q(InferenceMath.FormatNumber(g2Mean, 4)), Q(InferenceMath.FormatNumber(g2Sd, 4)),
            Q(InferenceMath.FormatNumber(r.MeanDifference, 4)), Q(InferenceMath.FormatNumber(r.TStatistic, 4)),
            Q(InferenceMath.FormatNumber(r.DegreesOfFreedom, 3)),
            Q(r.PValue is null ? "not calculated" : InferenceMath.FormatPValue(r.PValue.Value)),
            Q(InferenceMath.FormatNumber(r.CohensD, 4)), Q(InferenceMath.FormatNumber(r.HedgesG, 4)), Q("no")
        }));
        return sb.ToString();
    }
}

// ---------------------------------------------------------------------------
// Export: plain text and CSV of a computed one-way ANOVA result. Aggregate only
// — per-group sizes/means/SDs, the ANOVA table (SS/df/MS), F, p-value, and effect
// sizes. No participant-level data; no AI.
// ---------------------------------------------------------------------------
public static class OneWayAnovaExport
{
    public static string BuildPlainText(OneWayAnovaResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ONE-WAY ANOVA RESULT");
        sb.AppendLine($"Generated: {r.GeneratedDisplay}");
        sb.AppendLine($"Outcome:   {r.OutcomeDisplay}  (Continuous)");
        sb.AppendLine($"Grouping:  {r.GroupingDisplay}  (Categorical)");
        sb.AppendLine($"Complete valid observations (N): {r.ValidN}   (groups: {r.GroupCount}; excluded for missing: {r.DroppedForMissing}; non-numeric outcome dropped: {r.DroppedInvalid})");
        sb.AppendLine(new string('=', 78));

        if (r.Status == ParametricStatus.NotRunnable)
        {
            sb.AppendLine("This pairing could not be run.");
            sb.AppendLine(r.StatusReason);
            AppendNotes(sb, r);
            return sb.ToString();
        }

        if (r.Groups.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Groups (aggregate summary):");
            foreach (var g in r.Groups)
                sb.AppendLine($"  {g.Label}:  n = {g.N},  mean = {InferenceMath.FormatNumber(g.Mean, 3)},  SD = {InferenceMath.FormatNumber(g.Sd, 3)}");
        }

        sb.AppendLine();
        if (r.Status == ParametricStatus.CannotCompute)
        {
            sb.AppendLine("RESULT: Cannot compute a reliable result — needs review.");
            sb.AppendLine(r.StatusReason);
        }
        else
        {
            sb.AppendLine($"Test used: {r.TestUsed}");
            sb.AppendLine($"Grand mean: {InferenceMath.FormatNumber(r.GrandMean, 3)}");
            sb.AppendLine("ANOVA table:");
            sb.AppendLine($"  Between groups: SS = {InferenceMath.FormatNumber(r.SsBetween, 3)},  df = {r.DfBetween},  MS = {InferenceMath.FormatNumber(r.MsBetween, 3)}");
            sb.AppendLine($"  Within groups:  SS = {InferenceMath.FormatNumber(r.SsWithin, 3)},  df = {r.DfWithin},  MS = {InferenceMath.FormatNumber(r.MsWithin, 3)}");
            sb.AppendLine($"  Total:          SS = {InferenceMath.FormatNumber(r.SsTotal, 3)},  df = {r.DfBetween + r.DfWithin}");
            sb.AppendLine($"F({r.DfBetween}, {r.DfWithin}) = {InferenceMath.FormatNumber(r.FStatistic, 3)}");
            sb.AppendLine($"p-value: {r.PValueDisplay}");
            sb.AppendLine($"Effect size — eta-squared: {InferenceMath.FormatNumber(r.EtaSquared, 3)}   (omega-squared: {InferenceMath.FormatNumber(r.OmegaSquared, 3)})");
            sb.AppendLine($"Strength: {r.StrengthBand}");
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

    private static void AppendNotes(StringBuilder sb, OneWayAnovaResult r)
    {
        sb.AppendLine();
        sb.AppendLine("Notes:");
        foreach (var n in r.Notes) sb.AppendLine("  • " + n);
    }

    public static string BuildCsv(OneWayAnovaResult r)
    {
        var sb = new StringBuilder();
        string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

        sb.AppendLine(string.Join(",", new[]
        {
            "Test", "Outcome", "Grouping", "Status", "Groups", "ValidN", "MissingExcluded", "InvalidDropped",
            "GrandMean", "SSbetween", "dfBetween", "MSbetween", "SSwithin", "dfWithin", "MSwithin", "SStotal",
            "F", "p", "EtaSquared", "OmegaSquared", "AiInvolved"
        }.Select(Q)));

        sb.AppendLine(string.Join(",", new[]
        {
            Q(r.TestUsed), Q(r.OutcomeDisplay), Q(r.GroupingDisplay), Q(r.Status.ToString()),
            r.GroupCount.ToString(CultureInfo.InvariantCulture), r.ValidN.ToString(CultureInfo.InvariantCulture),
            r.DroppedForMissing.ToString(CultureInfo.InvariantCulture), r.DroppedInvalid.ToString(CultureInfo.InvariantCulture),
            Q(InferenceMath.FormatNumber(r.GrandMean, 4)),
            Q(InferenceMath.FormatNumber(r.SsBetween, 4)), r.DfBetween.ToString(CultureInfo.InvariantCulture), Q(InferenceMath.FormatNumber(r.MsBetween, 4)),
            Q(InferenceMath.FormatNumber(r.SsWithin, 4)), r.DfWithin.ToString(CultureInfo.InvariantCulture), Q(InferenceMath.FormatNumber(r.MsWithin, 4)),
            Q(InferenceMath.FormatNumber(r.SsTotal, 4)),
            Q(InferenceMath.FormatNumber(r.FStatistic, 4)),
            Q(r.PValue is null ? "not calculated" : InferenceMath.FormatPValue(r.PValue.Value)),
            Q(InferenceMath.FormatNumber(r.EtaSquared, 4)), Q(InferenceMath.FormatNumber(r.OmegaSquared, 4)), Q("no")
        }));

        // Second section: per-group aggregate summaries (no participant rows).
        if (r.Groups.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(string.Join(",", new[] { "Group", "n", "mean", "sd" }.Select(Q)));
            foreach (var g in r.Groups)
                sb.AppendLine(string.Join(",", new[]
                {
                    Q(g.Label), g.N.ToString(CultureInfo.InvariantCulture),
                    Q(InferenceMath.FormatNumber(g.Mean, 4)), Q(InferenceMath.FormatNumber(g.Sd, 4))
                }));
        }
        return sb.ToString();
    }
}
