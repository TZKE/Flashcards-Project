using System.Globalization;
using System.Text;

namespace AIFlashcardMaker;

// ===========================================================================
// Research Lab — Phase 4E (Slice 1): deterministic 2×2 MEASURES — Odds Ratio.
//
// 2×2 association measures for a binary outcome (event) versus a binary exposure
// (predictor). Slice 1 added the ODDS RATIO with a strict event/exposed-level
// resolver so a reversed table can never be produced silently. Slice 2 added the
// RISK RATIO and RISK DIFFERENCE point estimates, but ONLY for cohort/RCT (as
// risk) or cross-sectional (as prevalence) designs — case-control and unspecified
// designs suppress them. Slice 3 adds 95% CONFIDENCE INTERVALS: OR + ratio via
// the log (Woolf) method, difference via the approximate Wald method (ratio/diff
// CIs only when the ratio/diff is reported). NO exact/mid-P/bootstrap/profile CI.
//
// HARD RULES (audit-critical):
//   * Deterministic C# only (System.Math). Same inputs → bit-identical output.
//   * NO randomness, NO AI, NO HTTP, NO logging, NO file I/O. Consumes already-
//     loaded, in-memory data; raw participant rows never leave this device or
//     reach a log / AI. Only aggregate 2×2 counts + the odds ratio are exposed.
//   * ISOLATED — this file never modifies the categorical/Fisher/chi-square
//     engine. It only CALLS the already-public helpers
//     CategoricalInferenceEngine.FisherExact2x2 (association p) and
//     StatisticsVariablePreparer.ParseValueLabels (value labels). Nothing in
//     ResearchLabInference.cs is changed.
//   * ODDS RATIO + design-gated RISK/PREVALENCE RATIO & DIFFERENCE + their 95%
//     CIs only. NO regression, NO exact/mid-P/bootstrap/profile-likelihood CI.
//     RR/RD (and their CIs) are suppressed for case-control/unknown designs.
//   * The engine NEVER guesses the positive direction. If the event level or the
//     exposed level cannot be resolved deterministically it returns a
//     needs-level-review result and computes nothing.
//   * No WPF dependency — everything here is headless-testable.
// ===========================================================================

public enum TwoByTwoStatus
{
    Computed,        // a valid odds ratio was produced
    CannotCompute,   // valid-shaped but a guardrail blocks a reliable result
    NeedsLevelReview,// the positive event/exposed level could not be resolved
    NotRunnable      // the pairing is not eligible for 2×2 measures
}

// Conservative study-design classification for whether risk/prevalence measures
// may be reported. Only the analytic designs that can estimate risk/prevalence
// unlock RR/RD; everything else (including unspecified) is Unknown → suppressed.
public enum TwoByTwoStudyDesignKind
{
    Cohort,          // cohort / RCT / trial → risk (incidence) measures valid
    CrossSectional,  // cross-sectional → prevalence measures valid
    CaseControl,     // case-control → risk NOT estimable; OR only
    Unknown          // unspecified / not risk-estimable → RR/RD suppressed
}

public sealed class TwoByTwoMeasuresResult : IInferenceExportable
{
    public string OutcomeName { get; set; } = "";
    public string OutcomeDisplay { get; set; } = "";
    public string ExposureName { get; set; } = "";
    public string ExposureDisplay { get; set; } = "";
    public string PairTypeDisplay { get; set; } = "";

    public TwoByTwoStatus Status { get; set; } = TwoByTwoStatus.NotRunnable;
    public string StatusReason { get; set; } = "";
    public string TestUsed { get; set; } = "2×2 measures: odds ratio";

    // Resolved positive levels (displayed prominently — orientation guard).
    public string EventLevelDisplay { get; set; } = "";
    public string ExposedLevelDisplay { get; set; } = "";

    // Raw 2×2 cells in exposure×outcome orientation (aggregate only).
    //   a = exposed + event      b = exposed + no event
    //   c = unexposed + event    d = unexposed + no event
    public int A { get; set; }
    public int B { get; set; }
    public int C { get; set; }
    public int D { get; set; }
    public int N { get; set; }
    public int DroppedForMissing { get; set; }

    public bool CorrectionApplied { get; set; }
    public double? OddsRatio { get; set; }

    public double? AssociationP { get; set; }
    public string AssociationTest { get; set; } = "";

    // --- Slice 2: design-gated risk / prevalence measures. ------------------
    public string StudyDesignRaw { get; set; } = "";
    public TwoByTwoStudyDesignKind StudyDesignKind { get; set; } = TwoByTwoStudyDesignKind.Unknown;
    public string StudyDesignKindDisplay => StudyDesignKind switch
    {
        TwoByTwoStudyDesignKind.Cohort => "Cohort",
        TwoByTwoStudyDesignKind.CrossSectional => "Cross-sectional",
        TwoByTwoStudyDesignKind.CaseControl => "Case-control",
        _ => "Unknown"
    };

    // Populated only when AreRiskMeasuresReported (cohort / cross-sectional).
    public bool AreRiskMeasuresReported { get; set; }
    public string SuppressionReason { get; set; } = "";
    public double? EventProportionExposed { get; set; }     // raw a/(a+b)
    public double? EventProportionUnexposed { get; set; }   // raw c/(c+d)
    public string ProportionExposedLabel { get; set; } = "";   // "Risk in exposed" | "Prevalence in exposed"
    public string ProportionUnexposedLabel { get; set; } = "";
    public double? RatioMeasure { get; set; }               // risk ratio | prevalence ratio
    public double? DifferenceMeasure { get; set; }          // risk difference | prevalence difference (raw)
    public string RatioLabel { get; set; } = "";            // "Risk ratio" | "Prevalence ratio"
    public string DifferenceLabel { get; set; } = "";       // "Risk difference" | "Prevalence difference"
    public bool RatioUsesCorrection { get; set; }           // Haldane applied to the ratio (zero cell)

    // --- Slice 3: 95% confidence intervals. ---------------------------------
    // OR always (when computed); ratio/difference CIs only when reported.
    public string CiLevelDisplay => "95%";
    public double? OddsRatioCiLower { get; set; }
    public double? OddsRatioCiUpper { get; set; }
    public string OddsRatioCiMethod => "log (Woolf)";
    public bool OddsRatioCiUsesCorrection { get; set; }      // == CorrectionApplied when a CI was formed
    public double? RatioCiLower { get; set; }
    public double? RatioCiUpper { get; set; }
    public string RatioCiMethod => "log";
    public bool RatioCiUsesCorrection { get; set; }          // == RatioUsesCorrection when a CI was formed
    public double? DifferenceCiLower { get; set; }
    public double? DifferenceCiUpper { get; set; }
    public string DifferenceCiMethod => "Wald";              // approximate (raw counts, never corrected)
    public bool DifferenceCiUsesCorrection => false;

    // "0.354 to 101.556" | "—" when the interval could not be formed.
    public static string CiText(double? lo, double? hi) =>
        lo is null || hi is null ? "—"
        : $"{InferenceMath.FormatNumber(lo, 3)} to {InferenceMath.FormatNumber(hi, 3)}";

    public List<string> Assumptions { get; set; } = new();
    public List<string> Notes { get; set; } = new();

    public bool AiInvolved => false;
    public bool Computed => Status == TwoByTwoStatus.Computed;
    public string PValueDisplay => AssociationP is null ? "not calculated" : InferenceMath.FormatPValue(AssociationP.Value);
    public string OrDisplay => OddsRatio is null ? "—" : InferenceMath.FormatNumber(OddsRatio, 3);
    public string GeneratedDisplay => DateTime.UtcNow.ToLocalTime().ToString("MMM d, yyyy · h:mm tt", CultureInfo.InvariantCulture);

    // Odds ratio is the headline effect for this result.
    public string EffectName => "Odds ratio";
    public double? EffectValue => OddsRatio;

    // Plain-language direction (association, never causation).
    public string DirectionNote
    {
        get
        {
            if (OddsRatio is not { } or) return "";
            if (or >= 0.95 && or <= 1.05)
                return "The odds ratio is close to 1, suggesting little to no association between the exposure and the event (association, not causation).";
            return or > 1.0
                ? "The exposed group has HIGHER odds of the event than the unexposed group (association, not causation)."
                : "The exposed group has LOWER odds of the event than the unexposed group (association, not causation).";
        }
    }

    // IInferenceExportable.
    public string ResultTitle => $"{OutcomeDisplay}  ×  {ExposureDisplay}";
    public string ToPlainText() => TwoByTwoExport.BuildPlainText(this);
    public string ToCsv() => TwoByTwoExport.BuildCsv(this);
}

// ---------------------------------------------------------------------------
// The deterministic 2×2 measures engine (odds ratio + design-gated risk/
// prevalence ratio & difference, each with a 95% confidence interval).
// ---------------------------------------------------------------------------
public static class TwoByTwoMeasuresEngine
{
    public const int MinTotal = 4;   // a 2×2 below this is too sparse to report

    // Clearly-positive / clearly-negative level tokens (case-insensitive). A
    // level is resolved as positive ONLY when the tokens are unambiguous, or the
    // levels are exactly numeric 0/1 (1 = positive). Arbitrary numeric (1/2,
    // 2/3) or arbitrary text (A/B, Male/Female, Mild/Severe) is NEVER guessed.
    private static readonly HashSet<string> PositiveTokens = new(StringComparer.OrdinalIgnoreCase)
    { "yes", "y", "true", "positive", "pos", "present", "case", "disease", "diseased", "event", "dead", "death", "exposed", "smoker", "treated" };
    private static readonly HashSet<string> NegativeTokens = new(StringComparer.OrdinalIgnoreCase)
    { "no", "n", "false", "negative", "neg", "absent", "control", "healthy", "non-disease", "no disease", "alive", "unexposed", "non-smoker", "untreated" };

    public static bool IsRunnable(TestRecommendation? rec) => rec is not null && rec.CanCompute2x2Measures;

    private static bool IsBinaryKind(string k) => k == "Binary";

    public static TwoByTwoMeasuresResult Compute(
        TestRecommendation rec,
        ResearchVariable? outcome,
        ResearchVariable? predictor,
        StatisticsDataset? data,
        StatisticsMatchInput? match,
        string studyType = "")
    {
        var result = new TwoByTwoMeasuresResult { PairTypeDisplay = rec?.PairTypeDisplay ?? "" };
        result.StudyDesignRaw = (studyType ?? "").Trim();
        result.StudyDesignKind = ClassifyDesign(studyType);
        result.Notes.Add("Computed locally on this device by deterministic C# code. No AI was used to select or calculate this test.");
        result.Notes.Add("Only aggregate 2×2 counts and the measures below are shown or exported — no individual participant rows.");

        // --- Guard 1: eligibility. ------------------------------------------
        if (!IsRunnable(rec))
        {
            result.Status = TwoByTwoStatus.NotRunnable;
            result.StatusReason = "This pairing is not eligible for 2×2 measures. Both variables must be binary (two-level) and the plan must be ready to plan or need assumption review.";
            return result;
        }
        if (outcome is null || predictor is null || data is null || data.RowCount == 0 || match is null)
        {
            result.Status = TwoByTwoStatus.NotRunnable;
            result.StatusReason = "The dataset or matched variables were not available to compute this test.";
            return result;
        }
        if (!IsBinaryKind(rec!.OutcomeKind) || !IsBinaryKind(rec.PredictorKind))
        {
            result.Status = TwoByTwoStatus.NotRunnable;
            result.StatusReason = "2×2 measures need a binary outcome and a binary exposure.";
            return result;
        }

        result.OutcomeName = outcome.VariableName.Trim();
        result.OutcomeDisplay = Display(outcome);
        result.ExposureName = predictor.VariableName.Trim();
        result.ExposureDisplay = Display(predictor);

        match.VariableColumn.TryGetValue(outcome.Id, out string? oCol);
        match.VariableColumn.TryGetValue(predictor.Id, out string? pCol);
        int oIdx = data.ColumnIndexOf(oCol ?? "");
        int pIdx = data.ColumnIndexOf(pCol ?? "");
        if (oIdx < 0 || pIdx < 0)
        {
            result.Status = TwoByTwoStatus.NotRunnable;
            result.StatusReason = "The matched dataset columns for these variables could not be found.";
            return result;
        }

        // --- Assemble aligned (outcome, exposure) pairs (listwise deletion). -
        var pairs = new List<(string O, string P)>();
        var oLevels = new DistinctLevels();
        var pLevels = new DistinctLevels();
        for (int r = 0; r < data.RowCount; r++)
        {
            string ov = data.Cell(r, oIdx).Trim();
            string pv = data.Cell(r, pIdx).Trim();
            if (StatisticsMissingTokens.IsMissing(ov) || StatisticsMissingTokens.IsMissing(pv))
            {
                result.DroppedForMissing++;
                continue;
            }
            oLevels.Observe(ov);
            pLevels.Observe(pv);
            pairs.Add((ov, pv));
        }
        result.N = pairs.Count;

        // --- Guard 2: each variable must have exactly two observed levels. --
        if (oLevels.Count != 2 || pLevels.Count != 2)
        {
            result.Status = TwoByTwoStatus.CannotCompute;
            result.StatusReason = oLevels.Count != 2
                ? $"The outcome “{result.OutcomeDisplay}” has {oLevels.Count} observed level(s) after removing missing values; 2×2 measures need exactly two."
                : $"The exposure “{result.ExposureDisplay}” has {pLevels.Count} observed level(s) after removing missing values; 2×2 measures need exactly two.";
            AddMethodNotes(result);
            return result;
        }

        // --- Guard 3: minimum data. -----------------------------------------
        if (result.N < MinTotal)
        {
            result.Status = TwoByTwoStatus.CannotCompute;
            result.StatusReason = $"Only {result.N} complete pair(s) remain after removing missing values; at least {MinTotal} are needed for a 2×2 table.";
            AddMethodNotes(result);
            return result;
        }

        // --- Strict positive-level resolution (NEVER guessed). --------------
        var oMap = StatisticsVariablePreparer.ParseValueLabels(outcome.ValueLabels, outcome.Coding);
        var pMap = StatisticsVariablePreparer.ParseValueLabels(predictor.ValueLabels, predictor.Coding);
        bool eventOk = TryResolvePositive(oLevels.Values, oMap, out string eventPos, out string eventDisp);
        bool exposedOk = TryResolvePositive(pLevels.Values, pMap, out string exposedPos, out string exposedDisp);
        if (!eventOk || !exposedOk)
        {
            result.Status = TwoByTwoStatus.NeedsLevelReview;
            result.StatusReason =
                "2×2 measures need to know which outcome level is the event (the positive outcome you are counting) and which exposure level is the exposed group (versus the reference/unexposed group). "
                + (!eventOk ? $"The outcome “{result.OutcomeDisplay}” has two levels ({oLevels.Preview()}) and neither is clearly the event level. " : "")
                + (!exposedOk ? $"The exposure “{result.ExposureDisplay}” has two levels ({pLevels.Preview()}) and neither is clearly the exposed level. " : "")
                + "The analysis is paused here on purpose so the direction is not guessed — guessing which level is positive could invert the odds ratio. Set the coding or value labels (for example 1=Yes as the event/exposed level and 0=No as the reference), then re-run.";
            AddMethodNotes(result);
            return result;
        }
        result.EventLevelDisplay = eventDisp;
        result.ExposedLevelDisplay = exposedDisp;

        // --- Tally the 2×2 (exposure × outcome). ----------------------------
        int a = 0, b = 0, c = 0, d = 0;
        foreach (var (ov, pv) in pairs)
        {
            bool isEvent = string.Equals(ov, eventPos, StringComparison.OrdinalIgnoreCase);
            bool isExposed = string.Equals(pv, exposedPos, StringComparison.OrdinalIgnoreCase);
            if (isExposed && isEvent) a++;
            else if (isExposed) b++;
            else if (isEvent) c++;
            else d++;
        }
        result.A = a; result.B = b; result.C = c; result.D = d;

        // --- Guard 4: no empty margin (a correction can't invent a group). --
        if (a + b == 0 || c + d == 0 || a + c == 0 || b + d == 0)
        {
            result.Status = TwoByTwoStatus.CannotCompute;
            result.StatusReason =
                (a + b == 0 ? "No exposed participants were observed. "
                 : c + d == 0 ? "No unexposed participants were observed. "
                 : a + c == 0 ? "No events were observed. "
                 : "No non-events were observed. ")
                + "An odds ratio cannot be computed from an empty row or column.";
            AddMethodNotes(result);
            return result;
        }

        // --- Odds ratio (Haldane-Anscombe correction only for internal zero
        //     cells; every margin is already known to be > 0). ---------------
        bool zeroCell = a == 0 || b == 0 || c == 0 || d == 0;
        if (zeroCell)
        {
            result.CorrectionApplied = true;
            double a2 = a + 0.5, b2 = b + 0.5, c2 = c + 0.5, d2 = d + 0.5;
            result.OddsRatio = (a2 * d2) / (b2 * c2);
        }
        else
        {
            result.OddsRatio = ((double)a * d) / ((double)b * c);
        }
        if (result.OddsRatio is double or && (double.IsNaN(or) || double.IsInfinity(or)))
        {
            result.Status = TwoByTwoStatus.CannotCompute;
            result.StatusReason = "The odds ratio could not be computed from these counts.";
            AddMethodNotes(result);
            return result;
        }

        // --- Odds ratio 95% CI (log/Woolf), using the SAME counts as the OR
        //     point estimate (Haldane-corrected when a zero cell is present). --
        {
            double ca = zeroCell ? a + 0.5 : a, cb = zeroCell ? b + 0.5 : b,
                   cc = zeroCell ? c + 0.5 : c, cd = zeroCell ? d + 0.5 : d;
            double seLogOr = Math.Sqrt(1.0 / ca + 1.0 / cb + 1.0 / cc + 1.0 / cd);
            (result.OddsRatioCiLower, result.OddsRatioCiUpper) = LogCi(result.OddsRatio, seLogOr);
            result.OddsRatioCiUsesCorrection = zeroCell && result.OddsRatioCiLower is not null;
        }

        // --- Association p-value: the EXISTING public two-sided Fisher exact
        //     (always valid for a 2×2, including sparse cells). Uses raw counts;
        //     never modifies the categorical engine. --------------------------
        double fisher = CategoricalInferenceEngine.FisherExact2x2(a, b, c, d);
        if (!double.IsNaN(fisher) && !double.IsInfinity(fisher))
        {
            result.AssociationP = fisher;
            result.AssociationTest = "Fisher exact test (two-sided)";
        }

        // --- Slice 2: design-gated risk / prevalence measures. --------------
        ComputeRiskMeasures(result, a, b, c, d);

        result.Status = TwoByTwoStatus.Computed;
        AddAssumptions(result);
        AddMethodNotes(result);
        return result;
    }

    // Conservative design classifier. Case-insensitive, trimmed. Matches the
    // hyphenated "case-control" PHRASE (never bare "case", so "Case report" is
    // Unknown). Only cohort/RCT/trial (risk) and cross-sectional (prevalence)
    // unlock risk measures; retrospective / systematic review / meta-analysis /
    // case report / not-sure / blank → Unknown → suppressed.
    private static TwoByTwoStudyDesignKind ClassifyDesign(string? studyType)
    {
        string s = (studyType ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0) return TwoByTwoStudyDesignKind.Unknown;
        if (s.Contains("case-control") || s.Contains("case control") || s.Contains("case–control"))
            return TwoByTwoStudyDesignKind.CaseControl;
        if (s.Contains("cohort") || s.Contains("trial") || s.Contains("rct") || s.Contains("randomi"))
            return TwoByTwoStudyDesignKind.Cohort;
        if (s.Contains("cross"))   // cross-sectional / cross sectional
            return TwoByTwoStudyDesignKind.CrossSectional;
        return TwoByTwoStudyDesignKind.Unknown;
    }

    // Computes the observed event proportions and, ONLY for cohort/cross-sectional
    // designs, the ratio (risk/prevalence ratio) and difference. Case-control and
    // Unknown suppress RR/RD (with a reason). Margins are already guaranteed > 0.
    private static void ComputeRiskMeasures(TwoByTwoMeasuresResult r, int a, int b, int c, int d)
    {
        var design = r.StudyDesignKind;
        bool cohort = design == TwoByTwoStudyDesignKind.Cohort;
        bool crossSectional = design == TwoByTwoStudyDesignKind.CrossSectional;

        if (!cohort && !crossSectional)
        {
            r.AreRiskMeasuresReported = false;
            r.SuppressionReason = design == TwoByTwoStudyDesignKind.CaseControl
                ? "Risk ratio, risk difference, and their confidence intervals are not reported for a case-control design because incidence/risk cannot be estimated from case-control sampling. Use the odds ratio."
                : "Risk ratio, risk difference, and their confidence intervals are not reported because the study design is unspecified or not risk-estimable. Set the study design to Cohort or Cross-sectional if risk/prevalence measures are appropriate.";
            return;
        }

        // Raw observed event proportions (margins > 0, so always defined).
        double pExpRaw = (double)a / (a + b);
        double pUnexpRaw = (double)c / (c + d);
        r.EventProportionExposed = pExpRaw;
        r.EventProportionUnexposed = pUnexpRaw;
        r.AreRiskMeasuresReported = true;

        bool prevalence = crossSectional;   // cross-sectional → prevalence wording
        r.ProportionExposedLabel = prevalence ? "Prevalence in exposed" : "Risk in exposed";
        r.ProportionUnexposedLabel = prevalence ? "Prevalence in unexposed" : "Risk in unexposed";
        r.RatioLabel = prevalence ? "Prevalence ratio" : "Risk ratio";
        r.DifferenceLabel = prevalence ? "Prevalence difference" : "Risk difference";

        // Difference uses RAW counts (always defined).
        r.DifferenceMeasure = pExpRaw - pUnexpRaw;

        // Ratio: use Haldane-corrected counts when a zero cell would make it 0/∞;
        // otherwise the raw ratio. (CorrectionApplied is already the zero-cell flag.)
        if (r.CorrectionApplied)
        {
            double pExpC = (a + 0.5) / (a + b + 1.0);
            double pUnexpC = (c + 0.5) / (c + d + 1.0);
            r.RatioMeasure = pExpC / pUnexpC;
            r.RatioUsesCorrection = true;
        }
        else
        {
            r.RatioMeasure = pExpRaw / pUnexpRaw;
        }
        if (r.RatioMeasure is double rat && (double.IsNaN(rat) || double.IsInfinity(rat)))
            r.RatioMeasure = null;   // never emit NaN/Infinity

        // --- Slice 3: 95% CIs — ratio (log method), difference (Wald). ------
        // The ratio CI uses the SAME counts as the ratio point estimate
        // (Haldane-corrected on a zero cell); the difference CI uses RAW counts.
        {
            double ra = r.RatioUsesCorrection ? a + 0.5 : a;
            double rab = r.RatioUsesCorrection ? a + b + 1.0 : a + b;
            double rc = r.RatioUsesCorrection ? c + 0.5 : c;
            double rcd = r.RatioUsesCorrection ? c + d + 1.0 : c + d;
            double seLogRr = Math.Sqrt(1.0 / ra - 1.0 / rab + 1.0 / rc - 1.0 / rcd);
            (r.RatioCiLower, r.RatioCiUpper) = LogCi(r.RatioMeasure, seLogRr);
            r.RatioCiUsesCorrection = r.RatioUsesCorrection && r.RatioCiLower is not null;
        }
        {
            double seRd = Math.Sqrt(pExpRaw * (1 - pExpRaw) / (a + b) + pUnexpRaw * (1 - pUnexpRaw) / (c + d));
            (r.DifferenceCiLower, r.DifferenceCiUpper) = WaldCi(r.DifferenceMeasure, seRd);
        }
    }

    // 95% CI constant (z for a two-sided normal). A literal — NOT a new special
    // function and NOT an inverse-normal implementation.
    private const double Z = 1.959963984540054;

    // log/Woolf CI for a positive ratio: exp(ln(est) ± z·SE). Returns (null,null)
    // if the estimate is missing/non-positive or any bound is NaN/Infinity.
    private static (double? lo, double? hi) LogCi(double? estimate, double seLog)
    {
        if (estimate is not double e || e <= 0.0 || double.IsNaN(seLog) || double.IsInfinity(seLog)) return (null, null);
        double lnE = Math.Log(e);
        double lo = Math.Exp(lnE - Z * seLog), hi = Math.Exp(lnE + Z * seLog);
        if (double.IsNaN(lo) || double.IsInfinity(lo) || double.IsNaN(hi) || double.IsInfinity(hi)) return (null, null);
        return (lo, hi);
    }

    // Approximate Wald CI for a difference: est ± z·SE (NOT clamped to [-1,1]).
    private static (double? lo, double? hi) WaldCi(double? estimate, double se)
    {
        if (estimate is not double e || double.IsNaN(se) || double.IsInfinity(se)) return (null, null);
        double lo = e - Z * se, hi = e + Z * se;
        if (double.IsNaN(lo) || double.IsInfinity(lo) || double.IsNaN(hi) || double.IsInfinity(hi)) return (null, null);
        return (lo, hi);
    }

    // Resolves the positive level of a two-level variable. Returns false (block)
    // unless the positive direction is unambiguous (a clearly-positive token, or
    // exactly numeric 0/1 with 1 = positive). Never guesses arbitrary levels.
    private static bool TryResolvePositive(List<string> levels, Dictionary<string, string> labels, out string positive, out string display)
    {
        positive = ""; display = "";
        if (levels.Count != 2) return false;

        List<string> Candidates(string lvl)
        {
            var c = new List<string> { lvl };
            if (labels.TryGetValue(lvl, out string? lbl) && !string.IsNullOrWhiteSpace(lbl)) c.Add(lbl.Trim());
            return c;
        }
        bool IsPos(string lvl) => Candidates(lvl).Any(x => PositiveTokens.Contains(x));
        bool IsNeg(string lvl) => Candidates(lvl).Any(x => NegativeTokens.Contains(x));

        // A level carrying BOTH a positive and a negative token is contradictory.
        if (levels.Any(l => IsPos(l) && IsNeg(l))) return false;

        var posLevels = levels.Where(IsPos).ToList();
        if (posLevels.Count == 1)
        {
            positive = posLevels[0];
            display = Disp(positive, labels);
            return true;
        }
        // Numeric 0/1 fallback (only when tokens did not resolve a single positive).
        if (posLevels.Count == 0 && levels.Contains("0") && levels.Contains("1"))
        {
            positive = "1";
            display = Disp("1", labels);
            return true;
        }
        return false;   // ambiguous → block
    }

    private static string Disp(string level, Dictionary<string, string> labels)
        => labels.TryGetValue(level, out string? lbl) && !string.IsNullOrWhiteSpace(lbl) ? $"{level} = {lbl}" : level;

    private static void AddAssumptions(TwoByTwoMeasuresResult r)
    {
        r.Assumptions.Add($"Event = {r.EventLevelDisplay}; Exposed = {r.ExposedLevelDisplay}. Verify these match your intended coding — reversing Event or Exposed inverts the odds ratio.");
        r.Assumptions.Add("Independent observations: assumed (study-design level; not verifiable from the data).");
        r.Assumptions.Add("Both variables are binary (exactly two levels).");
        r.Assumptions.Add("The odds ratio measures ASSOCIATION, not causation.");
        r.Assumptions.Add("The odds ratio is valid for 2×2 association and for case-control designs. Risk ratio and risk difference are reported ONLY for cohort/RCT (as risk) or cross-sectional (as prevalence) designs, and are suppressed for case-control and unspecified designs.");
    }

    private static void AddMethodNotes(TwoByTwoMeasuresResult r)
    {
        if (r.CorrectionApplied && r.RatioUsesCorrection)
            r.Notes.Add("A zero cell was present; the odds ratio and risk/prevalence ratio (and their 95% CIs) use the Haldane-Anscombe correction (+0.5 to all cells). The risk/prevalence difference (and its 95% CI) and the displayed event proportions use the raw counts. Sparse-data estimates and intervals are unstable — interpret with caution. The raw counts are shown unchanged.");
        else if (r.CorrectionApplied)
            r.Notes.Add("A zero cell was present; the Haldane-Anscombe correction (+0.5 to all cells) was applied to the odds ratio and its 95% CI. Sparse-data estimates and intervals are unstable — interpret with caution. The raw counts are shown unchanged.");
        r.Notes.Add("Odds ratio = (a×d) ÷ (b×c) from the exposure×outcome 2×2 table (a = exposed+event, b = exposed+no event, c = unexposed+event, d = unexposed+no event). The association p-value is the two-sided Fisher exact test.");
        if (r.AreRiskMeasuresReported)
            r.Notes.Add("Risk/prevalence in exposed = a ÷ (a+b); in unexposed = c ÷ (c+d). Ratio = exposed ÷ unexposed; difference = exposed − unexposed (from raw counts).");
        r.Notes.Add("95% confidence intervals (z = 1.96): the odds ratio and risk/prevalence ratio use the log method (exp(ln(estimate) ± z·SE)); the risk/prevalence difference uses the approximate Wald method (estimate ± z·SE).");
        r.Notes.Add("An approximate Wald difference interval can extend beyond the logical −1 to +1 range, especially with sparse data or a proportion near 0 or 1; bounds are shown as computed (not clamped).");
        r.Notes.Add("p-values are never shown as 0; values below .001 are shown as \"< .001\". Full precision is kept internally.");
        r.Notes.Add("No regression is calculated in this version.");
    }

    // Display policy shared with the other engines (label when distinct, else
    // prettify an underscored name; the stored name is never modified).
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

    // Small distinct-level tracker: case-insensitive dedupe, keeps the first-seen
    // trimmed spelling, deterministic order of appearance.
    private sealed class DistinctLevels
    {
        private readonly Dictionary<string, string> _rep = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _order = new();

        public void Observe(string raw)
        {
            string key = raw.Trim();
            if (key.Length == 0) return;
            if (!_rep.ContainsKey(key)) { _rep[key] = key; _order.Add(key); }
        }

        public int Count => _order.Count;
        public List<string> Values => _order.ToList();
        public string Preview() => string.Join(", ", _order.Select(v => "“" + v + "”"));
    }
}

// ---------------------------------------------------------------------------
// Export: plain text and CSV of a computed 2×2 odds-ratio result. Aggregate
// only — the four cell counts, the resolved levels, the odds ratio, and the
// association p-value. No participant-level data; no AI.
// ---------------------------------------------------------------------------
public static class TwoByTwoExport
{
    public static string BuildPlainText(TwoByTwoMeasuresResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("2×2 MEASURES RESULT — ODDS RATIO");
        sb.AppendLine($"Generated: {r.GeneratedDisplay}");
        sb.AppendLine($"Outcome (event):    {r.OutcomeDisplay}");
        sb.AppendLine($"Exposure/predictor: {r.ExposureDisplay}");
        sb.AppendLine($"Complete valid pairs (N): {r.N}   (excluded for missing: {r.DroppedForMissing})");
        sb.AppendLine(new string('=', 78));

        if (r.Status == TwoByTwoStatus.NotRunnable)
        {
            sb.AppendLine("This pairing could not be run.");
            sb.AppendLine(r.StatusReason);
            AppendNotes(sb, r);
            return sb.ToString();
        }
        if (r.Status == TwoByTwoStatus.NeedsLevelReview)
        {
            sb.AppendLine("RESULT: Needs level review — the positive direction is unclear.");
            sb.AppendLine(r.StatusReason);
            AppendNotes(sb, r);
            return sb.ToString();
        }
        if (r.Status == TwoByTwoStatus.CannotCompute)
        {
            sb.AppendLine("RESULT: Cannot compute a reliable result — needs review.");
            sb.AppendLine(r.StatusReason);
            AppendNotes(sb, r);
            return sb.ToString();
        }

        sb.AppendLine($"Event-positive level:   {r.EventLevelDisplay}");
        sb.AppendLine($"Exposed-positive level: {r.ExposedLevelDisplay}");
        sb.AppendLine("Verify that Event and Exposed match your intended coding; reversing them can invert the Odds Ratio.");
        sb.AppendLine();
        sb.AppendLine("2×2 table (raw counts):");
        sb.AppendLine("                     Outcome +    Outcome -");
        sb.AppendLine($"  Exposure +           {r.A,-11}  {r.B,-11}");
        sb.AppendLine($"  Exposure -           {r.C,-11}  {r.D,-11}");
        sb.AppendLine("  (a = exposed+event, b = exposed+no event, c = unexposed+event, d = unexposed+no event)");
        sb.AppendLine();
        sb.AppendLine($"Test used: {r.TestUsed}");
        sb.AppendLine($"Odds Ratio: {r.OrDisplay}  ({r.CiLevelDisplay} CI {TwoByTwoMeasuresResult.CiText(r.OddsRatioCiLower, r.OddsRatioCiUpper)})"
            + (r.CorrectionApplied ? "  (Haldane-Anscombe corrected)" : ""));
        if (r.AssociationP is not null)
            sb.AppendLine($"Association p-value ({r.AssociationTest}): {r.PValueDisplay}");
        else
            sb.AppendLine("Association p-value: —");
        if (r.DirectionNote.Length > 0) sb.AppendLine($"Interpretation: {r.DirectionNote}");

        // --- Slice 2: study-design-gated risk / prevalence measures. --------
        sb.AppendLine();
        sb.AppendLine($"Study design: {r.StudyDesignKindDisplay}"
            + (r.StudyDesignRaw.Length > 0 && !string.Equals(r.StudyDesignRaw, r.StudyDesignKindDisplay, StringComparison.OrdinalIgnoreCase)
                ? $"  (from study type: {r.StudyDesignRaw})" : ""));
        if (r.AreRiskMeasuresReported)
        {
            sb.AppendLine($"{r.ProportionExposedLabel}:   {InferenceMath.FormatNumber(r.EventProportionExposed, 3)}");
            sb.AppendLine($"{r.ProportionUnexposedLabel}: {InferenceMath.FormatNumber(r.EventProportionUnexposed, 3)}");
            sb.AppendLine($"{r.RatioLabel}: {InferenceMath.FormatNumber(r.RatioMeasure, 3)}  ({r.CiLevelDisplay} CI {TwoByTwoMeasuresResult.CiText(r.RatioCiLower, r.RatioCiUpper)})"
                + (r.RatioUsesCorrection ? "  (Haldane-Anscombe corrected)" : ""));
            sb.AppendLine($"{r.DifferenceLabel}: {InferenceMath.FormatNumber(r.DifferenceMeasure, 3)}  ({r.CiLevelDisplay} CI {TwoByTwoMeasuresResult.CiText(r.DifferenceCiLower, r.DifferenceCiUpper)})");
            if (r.RatioMeasure is double rr)
            {
                string word = r.StudyDesignKind == TwoByTwoStudyDesignKind.CrossSectional ? "prevalence" : "risk";
                string dir = rr > 1.0 ? "higher" : rr < 1.0 ? "lower" : "similar";
                sb.AppendLine($"Interpretation: The exposed group has {dir} observed {word} of the event than the unexposed group (association, not causation).");
            }
            if (r.StudyDesignKind == TwoByTwoStudyDesignKind.CrossSectional)
                sb.AppendLine("These are prevalence measures, not incidence risk.");
        }
        else
        {
            sb.AppendLine(r.SuppressionReason);
            if (r.StudyDesignKind == TwoByTwoStudyDesignKind.CaseControl)
                sb.AppendLine("The odds ratio is the appropriate reported measure for this 2×2 design.");
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

    private static void AppendNotes(StringBuilder sb, TwoByTwoMeasuresResult r)
    {
        sb.AppendLine();
        sb.AppendLine("Notes:");
        foreach (var n in r.Notes) sb.AppendLine("  • " + n);
    }

    public static string BuildCsv(TwoByTwoMeasuresResult r)
    {
        var sb = new StringBuilder();
        string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

        string blankOrNum(double? v) => v is null ? "" : InferenceMath.FormatNumber(v, 4);
        string ratioCell = r.AreRiskMeasuresReported ? blankOrNum(r.RatioMeasure) : "Not reported";
        string diffCell = r.AreRiskMeasuresReported ? blankOrNum(r.DifferenceMeasure) : "Not reported";
        // CIs: OR CI always (when formed); ratio/difference CIs only when reported.
        string orCiLo = blankOrNum(r.OddsRatioCiLower), orCiHi = blankOrNum(r.OddsRatioCiUpper);
        string ratioCiLo = r.AreRiskMeasuresReported ? blankOrNum(r.RatioCiLower) : "Not reported";
        string ratioCiHi = r.AreRiskMeasuresReported ? blankOrNum(r.RatioCiUpper) : "Not reported";
        string diffCiLo = r.AreRiskMeasuresReported ? blankOrNum(r.DifferenceCiLower) : "Not reported";
        string diffCiHi = r.AreRiskMeasuresReported ? blankOrNum(r.DifferenceCiUpper) : "Not reported";
        string sparseNote = r.CorrectionApplied
            ? "Zero cell present; the Haldane-Anscombe correction was applied to the odds ratio, risk/prevalence ratio, and their 95% CIs. The difference and its CI use raw counts."
            : "";

        sb.AppendLine(string.Join(",", new[]
        {
            "Test", "Outcome", "Exposure", "Status", "EventLevel", "ExposedLevel",
            "a_exposed_event", "b_exposed_noevent", "c_unexposed_event", "d_unexposed_noevent",
            "N", "OddsRatio", "CorrectionApplied", "AssociationTest", "AssociationP",
            "StudyDesignRaw", "StudyDesignClassified",
            "ProportionExposedLabel", "ProportionUnexposedLabel", "ProportionExposed", "ProportionUnexposed",
            "RatioLabel", "RatioMeasure", "DifferenceLabel", "DifferenceMeasure",
            "OddsRatioCI_Lower", "OddsRatioCI_Upper", "RatioCI_Lower", "RatioCI_Upper", "DifferenceCI_Lower", "DifferenceCI_Upper",
            "CILevel", "CIMethod_OR", "CIMethod_Ratio", "CIMethod_Difference",
            "CIUsesCorrection_OR", "CIUsesCorrection_Ratio", "CIUsesCorrection_Difference", "SparseDataNote",
            "RiskMeasuresReported", "SuppressionReason", "RatioUsesCorrection", "AiInvolved"
        }.Select(Q)));

        sb.AppendLine(string.Join(",", new[]
        {
            Q(r.TestUsed), Q(r.OutcomeDisplay), Q(r.ExposureDisplay), Q(r.Status.ToString()),
            Q(r.EventLevelDisplay), Q(r.ExposedLevelDisplay),
            r.A.ToString(CultureInfo.InvariantCulture), r.B.ToString(CultureInfo.InvariantCulture),
            r.C.ToString(CultureInfo.InvariantCulture), r.D.ToString(CultureInfo.InvariantCulture),
            r.N.ToString(CultureInfo.InvariantCulture),
            Q(InferenceMath.FormatNumber(r.OddsRatio, 4)), Q(r.CorrectionApplied ? "yes" : "no"),
            Q(r.AssociationTest), Q(r.AssociationP is null ? "not calculated" : InferenceMath.FormatPValue(r.AssociationP.Value)),
            Q(r.StudyDesignRaw), Q(r.StudyDesignKindDisplay),
            Q(r.ProportionExposedLabel), Q(r.ProportionUnexposedLabel),
            Q(blankOrNum(r.EventProportionExposed)), Q(blankOrNum(r.EventProportionUnexposed)),
            Q(r.RatioLabel), Q(ratioCell), Q(r.DifferenceLabel), Q(diffCell),
            Q(orCiLo), Q(orCiHi), Q(ratioCiLo), Q(ratioCiHi), Q(diffCiLo), Q(diffCiHi),
            Q(r.CiLevelDisplay), Q(r.OddsRatioCiMethod), Q(r.RatioCiMethod), Q(r.DifferenceCiMethod),
            Q(r.OddsRatioCiUsesCorrection ? "yes" : "no"), Q(r.RatioCiUsesCorrection ? "yes" : "no"), Q(r.DifferenceCiUsesCorrection ? "yes" : "no"), Q(sparseNote),
            Q(r.AreRiskMeasuresReported ? "yes" : "no"), Q(r.SuppressionReason),
            Q(r.RatioUsesCorrection ? "yes" : "no"), Q("no")
        }));

        sb.AppendLine();
        sb.AppendLine(Q("Note") + "," + Q("95% CIs (z = 1.96): odds ratio and risk/prevalence ratio use the log method; risk/prevalence difference uses the approximate Wald method. No regression is calculated in this version."));
        return sb.ToString();
    }
}
