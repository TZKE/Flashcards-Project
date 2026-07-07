using System.Globalization;
using System.Text;

namespace AIFlashcardMaker;

// ===========================================================================
// Research Lab — Phase 4B Part 2 (MVP-1): deterministic CATEGORICAL inference.
//
// This is the first phase that actually COMPUTES an inferential result. It is
// intentionally the smallest safe slice: categorical-vs-categorical only
// (contingency table, chi-square test of independence, Fisher exact for
// 2×2 / small expected counts, Cramér's V, and phi for 2×2).
//
// HARD RULES (audit-critical):
//   * Deterministic C# only. Every number is produced by fixed algorithms in
//     this file using System.Math. Same inputs → bit-identical outputs.
//   * NO randomness anywhere (no Monte-Carlo / permutation tests). Every
//     p-value is closed-form (chi-square tail) or exact-summation (Fisher).
//   * NO AI, NO HTTP, NO network, NO logging, NO file I/O in this module. It
//     takes already-loaded, in-memory data and returns numbers. The raw CSV
//     rows never leave this device and are never sent to any AI or logged.
//   * Part 2 NEVER chooses a test. It only EXECUTES the test that Part 1
//     (TestRecommendationEngine) already recommended for a pairing, and only
//     when that pairing is Ready or Needs-assumption-review. Free-picking an
//     arbitrary test is structurally impossible — the only entry point takes a
//     TestRecommendation and re-checks its eligibility.
//   * No odds ratios, confidence intervals, t-test, ANOVA, Mann-Whitney,
//     Kruskal-Wallis, Pearson, Spearman, regression, or correlation
//     coefficients here. Those are later, separately-approved phases.
//   * No WPF dependency — everything here is headless-testable.
//
// Builds on Phase 4A/4B Part 1: the same StatisticsDataset, StatisticsMatchInput,
// missing-token rules, and value-label handling.
// ===========================================================================

// ---------------------------------------------------------------------------
// Deterministic math core (MVP-0). Special functions implemented from fixed
// series/continued-fraction algorithms — no external statistics package.
// ---------------------------------------------------------------------------
public static class InferenceMath
{
    private const int MaxIterations = 400;
    private const double Epsilon = 1e-14;
    private const double FpMin = 1e-300;   // guards continued-fraction underflow

    // Lanczos approximation to ln Γ(x) (g = 7, standard coefficients). Exact
    // and deterministic for all x > 0 used here (x = df/2, or integer + 1).
    public static double LogGamma(double x)
    {
        double[] c =
        {
            0.99999999999980993, 676.5203681218851, -1259.1392167224028,
            771.32342877765313, -176.61502916214059, 12.507343278686905,
            -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7
        };
        double xx = x - 1.0;
        double a = c[0];
        double t = xx + 7.5;
        for (int i = 1; i < c.Length; i++) a += c[i] / (xx + i);
        return 0.5 * Math.Log(2.0 * Math.PI) + (xx + 0.5) * Math.Log(t) - t + Math.Log(a);
    }

    // ln(n!) for n >= 0 via LogGamma(n + 1). Deterministic; log-space avoids
    // factorial overflow so Fisher exact stays stable for large tables.
    public static double LogFactorial(int n) => n <= 1 ? 0.0 : LogGamma(n + 1.0);

    // Lower regularized incomplete gamma P(a, x) via series (x < a+1). Returns
    // a value in [0, 1].
    private static double GammaSeries(double a, double x)
    {
        if (x <= 0) return 0.0;
        double ap = a;
        double sum = 1.0 / a;
        double del = sum;
        for (int n = 0; n < MaxIterations; n++)
        {
            ap += 1.0;
            del *= x / ap;
            sum += del;
            if (Math.Abs(del) < Math.Abs(sum) * Epsilon) break;
        }
        return sum * Math.Exp(-x + a * Math.Log(x) - LogGamma(a));
    }

    // Upper regularized incomplete gamma Q(a, x) via continued fraction
    // (x >= a+1). Returns a value in [0, 1].
    private static double GammaContinuedFraction(double a, double x)
    {
        double b = x + 1.0 - a;
        double c = 1.0 / FpMin;
        double d = 1.0 / b;
        double h = d;
        for (int i = 1; i <= MaxIterations; i++)
        {
            double an = -i * (i - a);
            b += 2.0;
            d = an * d + b;
            if (Math.Abs(d) < FpMin) d = FpMin;
            c = b + an / c;
            if (Math.Abs(c) < FpMin) c = FpMin;
            d = 1.0 / d;
            double del = d * c;
            h *= del;
            if (Math.Abs(del - 1.0) < Epsilon) break;
        }
        return Math.Exp(-x + a * Math.Log(x) - LogGamma(a)) * h;
    }

    public static double RegularizedGammaP(double a, double x)
    {
        if (a <= 0 || x < 0) return double.NaN;
        if (x == 0) return 0.0;
        return x < a + 1.0 ? GammaSeries(a, x) : 1.0 - GammaContinuedFraction(a, x);
    }

    public static double RegularizedGammaQ(double a, double x)
    {
        if (a <= 0 || x < 0) return double.NaN;
        if (x == 0) return 1.0;
        return x < a + 1.0 ? 1.0 - GammaSeries(a, x) : GammaContinuedFraction(a, x);
    }

    // Upper-tail p-value for a chi-square statistic with the given degrees of
    // freedom: P(X >= chiSquare) = Q(df/2, chiSquare/2). Guarded against
    // degenerate inputs — never returns NaN/Infinity.
    public static double ChiSquarePValue(double chiSquare, int df)
    {
        if (df < 1 || double.IsNaN(chiSquare) || chiSquare <= 0) return 1.0;
        double p = RegularizedGammaQ(df / 2.0, chiSquare / 2.0);
        if (double.IsNaN(p) || double.IsInfinity(p)) return 1.0;
        return Math.Clamp(p, 0.0, 1.0);
    }

    // Two-sided p-value for a standard-normal z-score, used by the Mann-Whitney
    // normal approximation. Derived from the SAME regularized incomplete gamma
    // as the chi-square tail — erfc(x) = Q(1/2, x²), so a two-sided normal tail
    // = erfc(|z|/√2) = Q(1/2, z²/2). This avoids adding a separate, unvalidated
    // erf approximation: it rides on the already-tested gamma core. Guarded to
    // never return NaN/Infinity.
    public static double NormalTwoSidedP(double z)
    {
        if (double.IsNaN(z)) return 1.0;
        double p = RegularizedGammaQ(0.5, z * z / 2.0);
        if (double.IsNaN(p) || double.IsInfinity(p)) return 1.0;
        return Math.Clamp(p, 0.0, 1.0);
    }

    // p-value display rules (audit requirement):
    //   * never display p = 0;
    //   * a p-value below .001 is shown as "< .001";
    //   * otherwise three decimals with a leading zero ("0.023").
    // Full precision is kept on the result object; this formats for UI/export.
    public static string FormatPValue(double p)
    {
        if (double.IsNaN(p)) return "—";
        p = Math.Clamp(p, 0.0, 1.0);
        if (p < 0.001) return "< .001";
        return p.ToString("0.000", CultureInfo.InvariantCulture);
    }

    // Generic small-number formatter (statistics, effect sizes) at full-ish
    // precision for reports; UI can re-round. Never emits NaN/Infinity text.
    public static string FormatNumber(double? v, int decimals = 3)
    {
        if (v is null || double.IsNaN(v.Value) || double.IsInfinity(v.Value)) return "—";
        return v.Value.ToString("0." + new string('0', Math.Max(0, decimals)), CultureInfo.InvariantCulture);
    }
}

// ---------------------------------------------------------------------------
// Shared export contract so the UI can hold, copy, and export any computed
// inference result (categorical, rank, …) through one reference. Every result
// type must be able to render itself as aggregate-only plain text and CSV.
// ---------------------------------------------------------------------------
public interface IInferenceExportable
{
    string ResultTitle { get; }
    bool Computed { get; }
    string ToPlainText();
    string ToCsv();
}

// ---------------------------------------------------------------------------
// Result models. Plain aggregate values only — no participant-level rows are
// ever stored on these objects, so nothing row-level can reach the UI/export.
// ---------------------------------------------------------------------------
public enum CategoricalTestStatus
{
    Computed,       // a valid p-value was produced (chi-square or Fisher exact)
    CannotCompute,  // table valid but assumptions unmet on a non-2×2 table → no p-value
    NotRunnable     // the pairing is not eligible (not categorical, or not Ready/assumption-review)
}

public sealed class ContingencyTable
{
    public string OutcomeName { get; set; } = "";     // rows
    public string PredictorName { get; set; } = "";   // columns
    public List<string> RowLabels { get; set; } = new();
    public List<string> ColumnLabels { get; set; } = new();
    public List<List<int>> Observed { get; set; } = new();      // [row][col]
    public List<List<double>> Expected { get; set; } = new();   // [row][col]
    public List<int> RowTotals { get; set; } = new();
    public List<int> ColumnTotals { get; set; } = new();
    public int GrandTotal { get; set; }

    public int RowCount => RowLabels.Count;
    public int ColCount => ColumnLabels.Count;
}

public sealed class CategoricalTestResult : IInferenceExportable
{
    public string OutcomeName { get; set; } = "";
    public string OutcomeDisplay { get; set; } = "";
    public string PredictorName { get; set; } = "";
    public string PredictorDisplay { get; set; } = "";
    public string PairTypeDisplay { get; set; } = "";

    public CategoricalTestStatus Status { get; set; } = CategoricalTestStatus.NotRunnable;
    public string StatusReason { get; set; } = "";
    public string TestUsed { get; set; } = "Not computed";

    public ContingencyTable? Table { get; set; }

    public int ValidPairs { get; set; }
    public int DroppedForMissing { get; set; }

    public double? ChiSquare { get; set; }        // full precision (null when not computed)
    public int DegreesOfFreedom { get; set; }
    public double? ChiSquarePValue { get; set; }  // full precision chi-square tail
    public double? FisherPValue { get; set; }     // full precision, 2×2 only
    public double? PValue { get; set; }           // the HEADLINE p-value actually used
    public double? CramersV { get; set; }
    public double? Phi { get; set; }              // 2×2 only

    // Assumption checklist (deterministic).
    public double MinExpected { get; set; }
    public int CellsBelow5 { get; set; }
    public int CellsBelow1 { get; set; }
    public int TotalCells { get; set; }
    public bool ExpectedCountsOk { get; set; }    // >= 80% of cells have expected >= 5
    public bool NoExpectedBelow1 { get; set; }
    public bool AssumptionsMet { get; set; }

    public List<string> Assumptions { get; set; } = new();
    public List<string> Notes { get; set; } = new();

    // Always false — surfaced so exports/UI can state it explicitly.
    public bool AiInvolved => false;

    public bool Computed => Status == CategoricalTestStatus.Computed;

    public string PValueDisplay => PValue is null ? "not calculated" : InferenceMath.FormatPValue(PValue.Value);
    public string GeneratedDisplay => DateTime.UtcNow.ToLocalTime().ToString("MMM d, yyyy · h:mm tt", CultureInfo.InvariantCulture);

    // IInferenceExportable — lets the UI hold/copy/export any result uniformly.
    public string ResultTitle => $"{OutcomeDisplay}  ×  {PredictorDisplay}";
    public string ToPlainText() => CategoricalInferenceExport.BuildPlainText(this);
    public string ToCsv() => CategoricalInferenceExport.BuildCsv(this);
}

// ---------------------------------------------------------------------------
// The deterministic categorical-inference engine.
// ---------------------------------------------------------------------------
public static class CategoricalInferenceEngine
{
    // Assumption thresholds (documented in the result's Assumptions list).
    public const double MinExpectedForChiSquare = 5.0;
    public const double ExpectedOkShare = 0.80;   // >= 80% of cells must have expected >= 5

    // Whether a deterministic categorical test can be COMPUTED for a pairing.
    // Single source of truth used by both the UI (run-button visibility) and
    // the engine guard below. Delegates to the Part 1 record so eligibility can
    // never drift from what the recommendation card actually shows.
    public static bool IsRunnable(TestRecommendation? rec) => rec is not null && rec.CanComputeCategorical;

    // Executes ONLY the recommended categorical test for one pairing. Returns a
    // NotRunnable result (never throws, never guesses) when the pairing is not
    // eligible or the data cannot be assembled.
    public static CategoricalTestResult Compute(
        TestRecommendation rec,
        ResearchVariable? outcome,
        ResearchVariable? predictor,
        StatisticsDataset? data,
        StatisticsMatchInput? match)
    {
        var result = new CategoricalTestResult
        {
            OutcomeName = rec?.OutcomeName ?? "",
            OutcomeDisplay = rec?.OutcomeDisplay ?? "",
            PredictorName = rec?.PredictorName ?? "",
            PredictorDisplay = rec?.PredictorDisplay ?? "",
            PairTypeDisplay = rec?.PairTypeDisplay ?? ""
        };
        result.Notes.Add("Computed locally on this device by deterministic C# code. No AI was used to select or calculate this test.");
        result.Notes.Add("Only aggregate category counts are shown or exported — no individual participant rows.");

        // --- Guard 1: eligibility. Only Ready / Needs-assumption-review
        // categorical×categorical pairings are ever computed here. -----------
        if (!IsRunnable(rec))
        {
            result.Status = CategoricalTestStatus.NotRunnable;
            result.StatusReason = "This pairing is not eligible for a categorical test. Only categorical-vs-categorical comparisons that are ready to plan or need assumption review can be computed.";
            return result;
        }
        if (outcome is null || predictor is null || data is null || data.RowCount == 0 || match is null)
        {
            result.Status = CategoricalTestStatus.NotRunnable;
            result.StatusReason = "The dataset or matched variables were not available to compute this test.";
            return result;
        }

        // --- Assemble aligned category pairs (listwise deletion). -----------
        match.VariableColumn.TryGetValue(outcome.Id, out string? oCol);
        match.VariableColumn.TryGetValue(predictor.Id, out string? pCol);
        int oIdx = data.ColumnIndexOf(oCol ?? "");
        int pIdx = data.ColumnIndexOf(pCol ?? "");
        if (oIdx < 0 || pIdx < 0)
        {
            result.Status = CategoricalTestStatus.NotRunnable;
            result.StatusReason = "The matched dataset columns for these variables could not be found.";
            return result;
        }

        var oLabels = StatisticsVariablePreparer.ParseValueLabels(outcome.ValueLabels, outcome.Coding);
        var pLabels = StatisticsVariablePreparer.ParseValueLabels(predictor.ValueLabels, predictor.Coding);

        var oCats = new CategoryAccumulator(oLabels);
        var pCats = new CategoryAccumulator(pLabels);
        var pairs = new List<(string ORaw, string PRaw)>();

        for (int r = 0; r < data.RowCount; r++)
        {
            string ov = data.Cell(r, oIdx).Trim();
            string pv = data.Cell(r, pIdx).Trim();
            if (StatisticsMissingTokens.IsMissing(ov) || StatisticsMissingTokens.IsMissing(pv))
            {
                result.DroppedForMissing++;
                continue;
            }
            oCats.Observe(ov);
            pCats.Observe(pv);
            pairs.Add((ov, pv));
        }
        result.ValidPairs = pairs.Count;

        var rowKeys = oCats.OrderedKeys();
        var colKeys = pCats.OrderedKeys();

        // --- Guard 2: table must be at least 2×2 with data. -----------------
        if (pairs.Count < 2 || rowKeys.Count < 2 || colKeys.Count < 2)
        {
            result.Status = CategoricalTestStatus.CannotCompute;
            result.StatusReason = rowKeys.Count < 2 || colKeys.Count < 2
                ? "One of the variables has only a single observed category after removing missing values, so there is nothing to compare."
                : "There are too few complete observations to build a comparison.";
            result.Table = BuildTableShell(result, oCats, pCats, rowKeys, colKeys, pairs);
            AddMethodNotes(result);
            return result;
        }

        // --- Contingency table + expected counts. ---------------------------
        var table = BuildTableShell(result, oCats, pCats, rowKeys, colKeys, pairs);
        result.Table = table;

        int rC = table.RowCount, cC = table.ColCount;
        result.TotalCells = rC * cC;
        double minExpected = double.MaxValue;
        int below5 = 0, below1 = 0;
        for (int i = 0; i < rC; i++)
            for (int j = 0; j < cC; j++)
            {
                double e = table.Expected[i][j];
                if (e < minExpected) minExpected = e;
                if (e < MinExpectedForChiSquare) below5++;
                if (e < 1.0) below1++;
            }
        result.MinExpected = minExpected;
        result.CellsBelow5 = below5;
        result.CellsBelow1 = below1;
        result.ExpectedCountsOk = (result.TotalCells - below5) >= ExpectedOkShare * result.TotalCells;
        result.NoExpectedBelow1 = below1 == 0;
        result.AssumptionsMet = result.ExpectedCountsOk && result.NoExpectedBelow1;

        // --- Chi-square statistic + degrees of freedom. ---------------------
        double chi2 = 0.0;
        for (int i = 0; i < rC; i++)
            for (int j = 0; j < cC; j++)
            {
                double e = table.Expected[i][j];       // > 0 (every included category has a positive margin)
                double diff = table.Observed[i][j] - e;
                chi2 += diff * diff / e;
            }
        int df = (rC - 1) * (cC - 1);
        result.ChiSquare = chi2;
        result.DegreesOfFreedom = df;
        result.ChiSquarePValue = InferenceMath.ChiSquarePValue(chi2, df);

        // --- Effect size (assumption-free association magnitude). ------------
        int n = table.GrandTotal;
        int k = Math.Min(rC, cC) - 1;
        result.CramersV = (n > 0 && k > 0) ? Math.Sqrt(chi2 / (n * (double)k)) : (double?)null;
        bool twoByTwo = rC == 2 && cC == 2;
        if (twoByTwo && n > 0) result.Phi = Math.Sqrt(chi2 / n);

        BuildAssumptionsList(result);

        // --- Decide the headline test. --------------------------------------
        if (twoByTwo)
        {
            // Fisher exact is always valid for a 2×2 table; compute it as an
            // exact cross-check, and use it as the headline when the chi-square
            // expected-count assumptions are not met.
            double fisher = FisherExact2x2(
                table.Observed[0][0], table.Observed[0][1],
                table.Observed[1][0], table.Observed[1][1]);
            result.FisherPValue = fisher;

            if (result.AssumptionsMet)
            {
                result.TestUsed = "Chi-square test of independence";
                result.PValue = result.ChiSquarePValue;
                result.Notes.Add("Fisher exact test is reported alongside as an exact cross-check.");
            }
            else
            {
                result.TestUsed = "Fisher exact test";
                result.PValue = fisher;
                result.Notes.Add("Expected cell counts are small, so the exact Fisher test is used as the headline result instead of chi-square. The chi-square value is shown for reference only.");
            }
            result.Status = CategoricalTestStatus.Computed;
            AddMethodNotes(result);
            return result;
        }

        // Larger than 2×2: Fisher exact is out of MVP scope, so a chi-square
        // whose expected-count assumptions fail must NOT be presented as a
        // p-value. Show the table + a clear "needs review" instead.
        if (result.AssumptionsMet)
        {
            result.TestUsed = "Chi-square test of independence";
            result.PValue = result.ChiSquarePValue;
            result.Status = CategoricalTestStatus.Computed;
        }
        else
        {
            result.TestUsed = "Not computed (expected counts too small)";
            result.PValue = null;
            result.Status = CategoricalTestStatus.CannotCompute;
            result.StatusReason =
                $"Some expected counts are too small for a reliable chi-square test on this {rC}×{cC} table " +
                $"(minimum expected {InferenceMath.FormatNumber(result.MinExpected, 2)}; {below5} of {result.TotalCells} cells below 5). " +
                "An exact test for larger tables is not available in this version. Consider collapsing sparse categories or collecting more data. " +
                "The table and association strength are shown, but no p-value is reported.";
        }
        AddMethodNotes(result);
        return result;
    }

    // ---- Contingency-table construction --------------------------------------
    private static ContingencyTable BuildTableShell(
        CategoricalTestResult result,
        CategoryAccumulator oCats, CategoryAccumulator pCats,
        List<string> rowKeys, List<string> colKeys,
        List<(string ORaw, string PRaw)> pairs)
    {
        var rowIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var colIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rowKeys.Count; i++) rowIndex[rowKeys[i]] = i;
        for (int j = 0; j < colKeys.Count; j++) colIndex[colKeys[j]] = j;

        var table = new ContingencyTable
        {
            OutcomeName = result.OutcomeName,
            PredictorName = result.PredictorName,
            RowLabels = rowKeys.Select(oCats.DisplayLabel).ToList(),
            ColumnLabels = colKeys.Select(pCats.DisplayLabel).ToList()
        };
        table.Observed = Enumerable.Range(0, rowKeys.Count).Select(_ => Enumerable.Repeat(0, colKeys.Count).ToList()).ToList();

        foreach (var (oRaw, pRaw) in pairs)
        {
            int i = rowIndex[oCats.Key(oRaw)];
            int j = colIndex[pCats.Key(pRaw)];
            table.Observed[i][j]++;
        }

        table.RowTotals = table.Observed.Select(row => row.Sum()).ToList();
        table.ColumnTotals = Enumerable.Range(0, colKeys.Count).Select(j => table.Observed.Sum(row => row[j])).ToList();
        table.GrandTotal = table.RowTotals.Sum();

        table.Expected = Enumerable.Range(0, rowKeys.Count)
            .Select(i => Enumerable.Range(0, colKeys.Count)
                .Select(j => table.GrandTotal == 0 ? 0.0 : (double)table.RowTotals[i] * table.ColumnTotals[j] / table.GrandTotal)
                .ToList())
            .ToList();
        return table;
    }

    // ---- Fisher exact test (two-sided) for a 2×2 table -----------------------
    // Exact: sums the hypergeometric probability of every table with the same
    // margins whose probability is <= the observed table's probability.
    // Deterministic (fixed enumeration, log-factorial probabilities).
    public static double FisherExact2x2(int a, int b, int c, int d)
    {
        int r1 = a + b, r2 = c + d, c1 = a + c, c2 = b + d, n = a + b + c + d;
        if (n <= 0) return 1.0;

        double logMarginConst =
            InferenceMath.LogFactorial(r1) + InferenceMath.LogFactorial(r2) +
            InferenceMath.LogFactorial(c1) + InferenceMath.LogFactorial(c2) -
            InferenceMath.LogFactorial(n);

        double LogProb(int aa)
        {
            int bb = r1 - aa, cc = c1 - aa, dd = r2 - cc;
            return logMarginConst
                   - InferenceMath.LogFactorial(aa) - InferenceMath.LogFactorial(bb)
                   - InferenceMath.LogFactorial(cc) - InferenceMath.LogFactorial(dd);
        }

        int aMin = Math.Max(0, c1 - r2);
        int aMax = Math.Min(r1, c1);
        if (aMax < aMin) return 1.0;

        double logObs = LogProb(a);
        // Small tolerance so ties are counted symmetrically despite float noise.
        double threshold = logObs + 1e-7;

        double p = 0.0;
        for (int aa = aMin; aa <= aMax; aa++)
        {
            double lp = LogProb(aa);
            if (lp <= threshold) p += Math.Exp(lp);
        }
        if (double.IsNaN(p) || double.IsInfinity(p)) return 1.0;
        return Math.Clamp(p, 0.0, 1.0);
    }

    // ---- Assumption + method notes ------------------------------------------
    private static void BuildAssumptionsList(CategoricalTestResult r)
    {
        r.Assumptions.Add($"Expected count ≥ 5 in at least 80% of cells: {(r.ExpectedCountsOk ? "met" : "not met")} " +
                          $"({r.TotalCells - r.CellsBelow5} of {r.TotalCells} cells ≥ 5).");
        r.Assumptions.Add($"No expected count below 1: {(r.NoExpectedBelow1 ? "met" : "not met")} " +
                          $"(minimum expected {InferenceMath.FormatNumber(r.MinExpected, 2)}).");
        r.Assumptions.Add("Independent observations: assumed (study-design level; not verifiable from the data).");
    }

    private static void AddMethodNotes(CategoricalTestResult r)
    {
        r.Notes.Add("Chi-square = Σ (observed − expected)² ÷ expected; expected = row total × column total ÷ grand total; df = (rows − 1) × (columns − 1).");
        r.Notes.Add("Chi-square p-value is the upper tail of the chi-square distribution (regularized incomplete gamma). Fisher exact is the two-sided sum of hypergeometric probabilities no larger than the observed table's.");
        r.Notes.Add("Cramér's V = √(χ² ÷ (N × (min(rows, columns) − 1))); for a 2×2 table this equals phi = √(χ² ÷ N).");
        r.Notes.Add("Rows missing either variable are excluded pairwise (listwise deletion). Categories are grouped case-insensitively after trimming whitespace; value labels are applied for display only.");
        r.Notes.Add("p-values are never shown as 0; values below .001 are shown as \"< .001\". Full precision is kept internally and rounded only for display.");
        r.Notes.Add("No odds ratio, confidence interval, or correlation is calculated in this version.");
    }

    // Groups raw category values case-insensitively (matching the descriptive
    // engine), tracks the most-frequent original spelling for display, applies
    // value labels when present, and orders categories deterministically.
    private sealed class CategoryAccumulator
    {
        private readonly Dictionary<string, string> _labels;                       // code → label
        private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, int>> _spellings = new(StringComparer.OrdinalIgnoreCase);

        public CategoryAccumulator(Dictionary<string, string> labels) => _labels = labels;

        public string Key(string raw) => raw.Trim();

        public void Observe(string raw)
        {
            string key = Key(raw);
            _counts[key] = _counts.TryGetValue(key, out int c) ? c + 1 : 1;
            if (!_spellings.TryGetValue(key, out var sp)) { sp = new(StringComparer.Ordinal); _spellings[key] = sp; }
            sp[raw] = sp.TryGetValue(raw, out int s) ? s + 1 : 1;
        }

        public string DisplayLabel(string key)
        {
            if (_labels.TryGetValue(key, out string? lbl) && !string.IsNullOrWhiteSpace(lbl))
                return $"{key} = {lbl}";
            // Most frequent original spelling, tie-broken by ordinal order for determinism.
            if (_spellings.TryGetValue(key, out var sp) && sp.Count > 0)
                return sp.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
            return key;
        }

        // Numeric-coded categories order numerically (handles 0/1 and ordinal
        // codes); otherwise by descending count, tie-broken deterministically.
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
// Export: plain text and CSV of a computed categorical result. Aggregate only —
// contingency counts, the test statistic, p-value, effect size, and assumptions.
// No participant-level data; no AI.
// ---------------------------------------------------------------------------
public static class CategoricalInferenceExport
{
    public static string BuildPlainText(CategoricalTestResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("CATEGORICAL ANALYSIS RESULT");
        sb.AppendLine($"Generated: {r.GeneratedDisplay}");
        sb.AppendLine($"Outcome:   {r.OutcomeDisplay}");
        sb.AppendLine($"Predictor: {r.PredictorDisplay}");
        sb.AppendLine($"Pairing:   {r.PairTypeDisplay}");
        sb.AppendLine($"Valid observations: {r.ValidPairs}   (excluded for missing: {r.DroppedForMissing})");
        sb.AppendLine(new string('=', 78));

        if (r.Status == CategoricalTestStatus.NotRunnable)
        {
            sb.AppendLine("This pairing could not be run.");
            sb.AppendLine(r.StatusReason);
            AppendNotes(sb, r);
            return sb.ToString();
        }

        if (r.Table is { } t) AppendTable(sb, t);

        sb.AppendLine();
        if (r.Status == CategoricalTestStatus.CannotCompute)
        {
            sb.AppendLine("RESULT: Cannot compute a reliable p-value — needs review.");
            sb.AppendLine(r.StatusReason);
        }
        else
        {
            sb.AppendLine($"Test used: {r.TestUsed}");
            if (r.ChiSquare is not null)
                sb.AppendLine($"Chi-square: {InferenceMath.FormatNumber(r.ChiSquare, 3)}   df: {r.DegreesOfFreedom}   " +
                              $"chi-square p: {InferenceMath.FormatPValue(r.ChiSquarePValue ?? double.NaN)}");
            if (r.FisherPValue is not null)
                sb.AppendLine($"Fisher exact p (two-sided): {InferenceMath.FormatPValue(r.FisherPValue.Value)}");
            sb.AppendLine($"Headline p-value: {r.PValueDisplay}");
        }

        sb.AppendLine();
        sb.AppendLine("Association strength (descriptive):");
        sb.AppendLine($"  Cramér's V: {InferenceMath.FormatNumber(r.CramersV, 3)}" +
                      (r.Phi is not null ? $"   phi: {InferenceMath.FormatNumber(r.Phi, 3)}" : ""));

        sb.AppendLine();
        sb.AppendLine("Assumptions:");
        foreach (var a in r.Assumptions) sb.AppendLine("  - " + a);

        AppendNotes(sb, r);
        return sb.ToString();
    }

    private static void AppendTable(StringBuilder sb, ContingencyTable t)
    {
        sb.AppendLine();
        sb.AppendLine("Contingency table (observed counts; expected in parentheses):");
        int w = Math.Max(12, t.ColumnLabels.DefaultIfEmpty("").Max(c => c.Length) + 2);
        string Cell(string s) => s.Length >= w ? s.Substring(0, w - 1) + " " : s.PadRight(w);

        sb.Append(Cell(""));
        foreach (var c in t.ColumnLabels) sb.Append(Cell(c));
        sb.AppendLine(Cell("Total"));

        for (int i = 0; i < t.RowCount; i++)
        {
            sb.Append(Cell(t.RowLabels[i]));
            for (int j = 0; j < t.ColCount; j++)
                sb.Append(Cell($"{t.Observed[i][j]} ({InferenceMath.FormatNumber(t.Expected[i][j], 1)})"));
            sb.AppendLine(Cell(t.RowTotals[i].ToString(CultureInfo.InvariantCulture)));
        }
        sb.Append(Cell("Total"));
        foreach (var ct in t.ColumnTotals) sb.Append(Cell(ct.ToString(CultureInfo.InvariantCulture)));
        sb.AppendLine(Cell(t.GrandTotal.ToString(CultureInfo.InvariantCulture)));
    }

    private static void AppendNotes(StringBuilder sb, CategoricalTestResult r)
    {
        sb.AppendLine();
        sb.AppendLine("Notes:");
        foreach (var n in r.Notes) sb.AppendLine("  • " + n);
    }

    public static string BuildCsv(CategoricalTestResult r)
    {
        var sb = new StringBuilder();
        string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

        sb.AppendLine(string.Join(",", new[]
        {
            "Outcome", "Predictor", "Pairing", "Status", "TestUsed", "ValidN", "MissingExcluded",
            "ChiSquare", "df", "ChiSquareP", "FisherP", "HeadlineP",
            "CramersV", "Phi", "MinExpected", "CellsBelow5", "TotalCells", "AssumptionsMet", "AiInvolved"
        }.Select(Q)));

        sb.AppendLine(string.Join(",", new[]
        {
            Q(r.OutcomeDisplay), Q(r.PredictorDisplay), Q(r.PairTypeDisplay), Q(r.Status.ToString()), Q(r.TestUsed),
            r.ValidPairs.ToString(CultureInfo.InvariantCulture), r.DroppedForMissing.ToString(CultureInfo.InvariantCulture),
            Q(InferenceMath.FormatNumber(r.ChiSquare, 4)), r.DegreesOfFreedom.ToString(CultureInfo.InvariantCulture),
            Q(r.ChiSquarePValue is null ? "" : InferenceMath.FormatPValue(r.ChiSquarePValue.Value)),
            Q(r.FisherPValue is null ? "" : InferenceMath.FormatPValue(r.FisherPValue.Value)),
            Q(r.PValue is null ? "not calculated" : InferenceMath.FormatPValue(r.PValue.Value)),
            Q(InferenceMath.FormatNumber(r.CramersV, 4)), Q(r.Phi is null ? "" : InferenceMath.FormatNumber(r.Phi, 4)),
            Q(InferenceMath.FormatNumber(r.MinExpected, 3)), r.CellsBelow5.ToString(CultureInfo.InvariantCulture),
            r.TotalCells.ToString(CultureInfo.InvariantCulture), Q(r.AssumptionsMet ? "yes" : "no"), Q("no")
        }));

        // Second section: the contingency table (aggregate counts only).
        if (r.Table is { } t)
        {
            sb.AppendLine();
            sb.AppendLine(string.Join(",", new[] { Q("Observed counts") }));
            sb.AppendLine(string.Join(",", new[] { Q("") }.Concat(t.ColumnLabels.Select(Q)).Concat(new[] { Q("Total") })));
            for (int i = 0; i < t.RowCount; i++)
                sb.AppendLine(string.Join(",", new[] { Q(t.RowLabels[i]) }
                    .Concat(t.Observed[i].Select(v => v.ToString(CultureInfo.InvariantCulture)))
                    .Concat(new[] { t.RowTotals[i].ToString(CultureInfo.InvariantCulture) })));
            sb.AppendLine(string.Join(",", new[] { Q("Total") }
                .Concat(t.ColumnTotals.Select(v => v.ToString(CultureInfo.InvariantCulture)))
                .Concat(new[] { t.GrandTotal.ToString(CultureInfo.InvariantCulture) })));
        }
        return sb.ToString();
    }
}
