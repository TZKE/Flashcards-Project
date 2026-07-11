using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace AIFlashcardMaker;

// ===========================================================================
// Research Lab — Phase 4B Part 1: deterministic statistical-test RECOMMENDATION
// planning. This phase RECOMMENDS which inferential test would be appropriate
// in the future; it does NOT run any test.
//
// HARD RULES (audit-critical):
//   * NO inferential calculation of any kind: no p-values, confidence
//     intervals, odds ratios, regression/correlation coefficients, chi-square,
//     t-test, ANOVA, Mann-Whitney, Kruskal-Wallis, Fisher, Pearson, Spearman.
//     Only NAMES of future tests + deterministic reasoning are produced.
//   * Code decides the recommendation. There is NO AI involvement here and no
//     AI may choose or override a test.
//   * No raw participant rows are logged or sent anywhere. Only variable
//     metadata + aggregate group/valid/missing counts are used.
//   * No WPF dependency — everything here is headless-testable.
//
// It builds on Phase 4A: the same extraction-sheet metadata, full local
// dataset, variable matching, and readiness gate.
// ===========================================================================

// Analysis "family" a variable falls into for test selection. Derived from the
// Extraction Sheet type/level (source of truth) plus the OBSERVED number of
// non-missing categories in the local dataset.
public enum RecoVarKind
{
    Continuous,     // continuous / scale numeric
    Binary,         // categorical-family with exactly 2 observed categories
    Nominal,        // categorical-family with 3+ observed categories (unordered)
    Ordinal,        // ordered categories
    Unsupported,    // text / date / identifier — not suitable for these tests
    Ambiguous       // metadata unclear, single observed group, or too little data
}

public enum TestRecoStatus
{
    Ready,                  // role is a clear predictor AND the test carries no unverified assumptions beyond basic study design
    NeedsAssumptionReview,  // test depends on distributional/relationship assumptions to be checked later
    NeedsRoleReview,        // the variable's role is unclear or outcome-like — confirm before treating it as a predictor
    NeedsMetadataReview,    // extraction-sheet metadata (type/level) must be clarified first
    NotRecommended,         // structurally cannot be compared (e.g. single group, too few observations)
    Unsupported             // variable type not suitable for this engine
}

// How a variable's Extraction-Sheet role maps onto test-recommendation intent.
public enum RecoRoleClass
{
    Primary,        // predictor / exposure / group / independent variable / risk factor / confounder-covariate
    OutcomeLike,    // a second outcome / secondary / dependent variable → review before using as a predictor
    Unclear,        // blank / unknown / other / demographic / eligibility → confirm the role
    Excluded        // identifier / metadata → never a predictor (no card)
}

// One recommended future test for an (outcome, predictor) pair.
public sealed class TestRecommendation
{
    public string OutcomeName { get; set; } = "";
    public string OutcomeDisplay { get; set; } = "";
    public string PredictorName { get; set; } = "";
    public string PredictorDisplay { get; set; } = "";

    public string OutcomeKind { get; set; } = "";       // e.g. "Binary", "Continuous"
    public string PredictorKind { get; set; } = "";
    public string PairTypeDisplay { get; set; } = "";   // "Binary outcome vs ordinal predictor"

    public string PredictorRole { get; set; } = "";     // raw Extraction-Sheet role for display ("Predictor", "Unknown", …)
    public bool AssumptionDependent { get; set; }       // the planned test depends on unverified distributional/relationship assumptions

    public int OutcomeGroups { get; set; }
    public int PredictorGroups { get; set; }
    public int OutcomeValidN { get; set; }
    public int OutcomeMissingN { get; set; }
    public int PredictorValidN { get; set; }
    public int PredictorMissingN { get; set; }

    public string RecommendedTest { get; set; } = "";
    public string AlternativeTest { get; set; } = "";   // optional secondary option
    public string Rationale { get; set; } = "";
    public List<string> Checklist { get; set; } = new();
    public List<string> Notes { get; set; } = new();

    public TestRecoStatus Status { get; set; } = TestRecoStatus.NeedsMetadataReview;

    // Always true in Phase 4B Part 1 — surfaced on every card so the user is
    // never misled into thinking a result was computed.
    public bool NoPValueThisPhase { get; set; } = true;

    [JsonIgnore]
    public string StatusDisplay => Status switch
    {
        TestRecoStatus.Ready => "Ready for future inferential analysis",
        TestRecoStatus.NeedsAssumptionReview => "Needs assumption review",
        TestRecoStatus.NeedsRoleReview => "Needs role review",
        TestRecoStatus.NeedsMetadataReview => "Needs variable metadata review",
        TestRecoStatus.NotRecommended => "Not recommended",
        _ => "Unsupported for now"
    };

    // Card accent colour bucket for the UI: "Good" | "Warn" | "Muted" | "Bad".
    [JsonIgnore]
    public string StatusKind => Status switch
    {
        TestRecoStatus.Ready => "Good",
        TestRecoStatus.NeedsAssumptionReview or TestRecoStatus.NeedsRoleReview or TestRecoStatus.NeedsMetadataReview => "Warn",
        TestRecoStatus.NotRecommended => "Bad",
        _ => "Muted"
    };

    // Grouping bucket for the review list + export sections.
    [JsonIgnore]
    public string GroupDisplay => Status switch
    {
        TestRecoStatus.Ready => "Ready to plan",
        TestRecoStatus.NeedsAssumptionReview => "Needs assumption review",
        TestRecoStatus.NeedsRoleReview => "Needs role review",
        _ => "Unsupported / not recommended"
    };
    [JsonIgnore]
    public int GroupOrder => Status switch
    {
        TestRecoStatus.Ready => 0,
        TestRecoStatus.NeedsAssumptionReview => 1,
        TestRecoStatus.NeedsRoleReview => 2,
        _ => 3
    };

    // The purple sub-header on the card: makes it clear an assumption-dependent
    // recommendation is a planned OPTION, not a settled choice or a result.
    [JsonIgnore]
    public string PlanningLabel => AssumptionDependent ? "Recommended future test — pending assumption review" : "Recommended future test";

    [JsonIgnore] public string TestDisplay => string.IsNullOrWhiteSpace(AlternativeTest) ? RecommendedTest : $"{RecommendedTest}  ·  or  {AlternativeTest}";

    // Phase 4B Part 2 (MVP-1) eligibility — METADATA ONLY, not a calculation.
    // A card's categorical test may be COMPUTED only when both variables are
    // categorical-family (Binary/Categorical) AND the plan is Ready or
    // Needs-assumption-review. Role/metadata review, "not recommended", and
    // unsupported/continuous/ordinal pairings are never runnable in this phase,
    // so those cards never get a Run button. No p-value is produced here.
    [JsonIgnore]
    public bool CanComputeCategorical =>
        (Status == TestRecoStatus.Ready || Status == TestRecoStatus.NeedsAssumptionReview)
        && IsCategoricalKindDisplay(OutcomeKind) && IsCategoricalKindDisplay(PredictorKind);

    private static bool IsCategoricalKindDisplay(string kind) =>
        string.Equals(kind, "Binary", StringComparison.Ordinal)
        || string.Equals(kind, "Categorical", StringComparison.Ordinal);

    // Phase 4B Part 2 (MVP-2) eligibility — METADATA ONLY, not a calculation.
    // A card's rank test (Mann-Whitney U / Kruskal-Wallis) may be COMPUTED only
    // when exactly one variable is rankable (Ordinal/Continuous) and the other
    // is a grouping variable (Binary/Categorical), AND the plan is Ready or
    // Needs-assumption-review. Handles both orientations (the ordinal/continuous
    // side may be the outcome OR the predictor). Ordinal outcomes run the
    // headline recommendation; continuous outcomes run the robust alternative.
    [JsonIgnore]
    public bool CanComputeRank =>
        (Status == TestRecoStatus.Ready || Status == TestRecoStatus.NeedsAssumptionReview)
        && ((IsRankableKindDisplay(OutcomeKind) && IsCategoricalKindDisplay(PredictorKind))
            || (IsRankableKindDisplay(PredictorKind) && IsCategoricalKindDisplay(OutcomeKind)));

    private static bool IsRankableKindDisplay(string kind) =>
        string.Equals(kind, "Ordinal", StringComparison.Ordinal)
        || string.Equals(kind, "Continuous", StringComparison.Ordinal);

    // Phase 4B Part 2 (MVP-3) eligibility — METADATA ONLY, not a calculation.
    // A card's Spearman correlation may be COMPUTED only when BOTH variables are
    // rankable (Ordinal/Continuous) AND the plan is Ready or Needs-assumption-
    // review. Ordinal-involving pairs run the headline recommendation; a
    // continuous×continuous pair runs the robust alternative (Pearson is not
    // computed). Mutually exclusive with CanComputeCategorical and
    // CanComputeRank (which require a categorical/grouping side).
    [JsonIgnore]
    public bool CanComputeSpearman =>
        (Status == TestRecoStatus.Ready || Status == TestRecoStatus.NeedsAssumptionReview)
        && IsRankableKindDisplay(OutcomeKind) && IsRankableKindDisplay(PredictorKind);

    // Phase 4D (Slice 1) eligibility — METADATA ONLY, not a calculation.
    // A card's Welch t-test may be COMPUTED only when the OUTCOME is continuous
    // and the PREDICTOR is binary (a two-level grouping variable — note a
    // two-level categorical is classified as "Binary" kind) AND the plan is
    // Ready or Needs-assumption-review. This is a SUBSET of CanComputeRank
    // (a continuous outcome × binary group is also rank-eligible for the MWU
    // robust alternative), so dispatch runs Welch first: Welch is the headline
    // recommendation for this pairing, MWU is only its named alternative.
    // Binary/ordinal outcomes and continuous×continuous are never Welch.
    [JsonIgnore]
    public bool CanComputeWelch =>
        (Status == TestRecoStatus.Ready || Status == TestRecoStatus.NeedsAssumptionReview)
        && IsContinuousKindDisplay(OutcomeKind) && IsBinaryKindDisplay(PredictorKind);

    private static bool IsContinuousKindDisplay(string kind) =>
        string.Equals(kind, "Continuous", StringComparison.Ordinal);

    private static bool IsBinaryKindDisplay(string kind) =>
        string.Equals(kind, "Binary", StringComparison.Ordinal);

    // Phase 4D (Slice 2) eligibility — METADATA ONLY, not a calculation.
    // A card's one-way ANOVA may be COMPUTED only when the OUTCOME is continuous
    // and the PREDICTOR is a categorical grouping variable with 3+ observed groups
    // (kind "Categorical" — a two-level grouping is kind "Binary" and runs the
    // Welch t-test instead) AND the plan is Ready or Needs-assumption-review.
    // Like Welch, this is a SUBSET of CanComputeRank, so dispatch runs ANOVA
    // BEFORE Rank: ANOVA is the headline recommendation for a continuous outcome
    // across 3+ groups, and the Kruskal-Wallis test is only its named robust
    // alternative. Mutually exclusive with CanComputeWelch (which needs a Binary
    // predictor). Binary/ordinal outcomes and continuous×continuous are never ANOVA.
    [JsonIgnore]
    public bool CanComputeAnova =>
        (Status == TestRecoStatus.Ready || Status == TestRecoStatus.NeedsAssumptionReview)
        && IsContinuousKindDisplay(OutcomeKind) && IsNominalKindDisplay(PredictorKind);

    private static bool IsNominalKindDisplay(string kind) =>
        string.Equals(kind, "Categorical", StringComparison.Ordinal);

    // Phase 4D (Slice 3) eligibility — METADATA ONLY, not a calculation.
    // A card's Pearson correlation may be COMPUTED only when BOTH variables are
    // continuous AND the plan is Ready or Needs-assumption-review. Pearson is the
    // headline PARAMETRIC correlation for a continuous×continuous pairing; it is a
    // SUBSET of CanComputeSpearman (both-continuous ⊂ both-rankable), so dispatch
    // runs Pearson BEFORE the Spearman fallback and Spearman becomes the named
    // robust nonparametric alternative. Any ordinal-involving pair is never
    // Pearson (it stays Spearman). Mutually exclusive with the grouping-based
    // gates (Categorical/Rank/Welch/ANOVA), which all require a categorical side.
    [JsonIgnore]
    public bool CanComputePearson =>
        (Status == TestRecoStatus.Ready || Status == TestRecoStatus.NeedsAssumptionReview)
        && IsContinuousKindDisplay(OutcomeKind) && IsContinuousKindDisplay(PredictorKind);
}

// The whole Recommended Analysis result for one project.
public sealed class TestRecommendationResult
{
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public string OutcomeName { get; set; } = "";
    public string OutcomeDisplay { get; set; } = "";
    public List<TestRecommendation> Recommendations { get; set; } = new();
    public List<string> GlobalNotes { get; set; } = new();

    public int CandidatePredictors => Recommendations.Count;
    public int ReadyCount => Recommendations.Count(r => r.Status == TestRecoStatus.Ready);
    public int AssumptionReviewCount => Recommendations.Count(r => r.Status == TestRecoStatus.NeedsAssumptionReview);
    public int RoleReviewCount => Recommendations.Count(r => r.Status == TestRecoStatus.NeedsRoleReview);
    public int UnsupportedCount => Recommendations.Count(r => r.Status is TestRecoStatus.Unsupported or TestRecoStatus.NotRecommended or TestRecoStatus.NeedsMetadataReview);
    // Kept for backward-compat with earlier callers/tests.
    public int NeedsReviewCount => AssumptionReviewCount + RoleReviewCount;

    [JsonIgnore]
    public string GeneratedDisplay => GeneratedAt.ToLocalTime().ToString("MMM d, yyyy · h:mm tt", CultureInfo.InvariantCulture);
}

// One line of the "Recommended Analysis locked" checklist.
public sealed class RecommendationGateItem
{
    public string Label { get; set; } = "";
    public bool Passed { get; set; }
    [JsonIgnore] public string Glyph => Passed ? "✓" : "•";
    [JsonIgnore] public string Kind => Passed ? "Good" : "Bad";
}

public sealed class RecommendationGate
{
    public bool Locked { get; set; }
    public bool HasWarnings { get; set; }             // Phase 4A NeedsReview → yellow banner when unlocked
    public string PrimaryReason { get; set; } = "";   // shown at the top of the locked state
    public List<RecommendationGateItem> Checklist { get; set; } = new();
}

// ---------------------------------------------------------------------------
// The deterministic engine.
// ---------------------------------------------------------------------------
public static class TestRecommendationEngine
{
    // Minimum non-missing observations before a comparison is even worth
    // planning. Below this, the pairing is "Not recommended" (too little data).
    public const int MinValidObservations = 6;
    public const double HighMissingPercent = 50.0;

    // ---- Gate: is Recommended Analysis available yet? -----------------------
    public static RecommendationGate EvaluateGate(
        IReadOnlyList<ResearchVariable> variables,
        StatisticsDataset? data,
        StatisticsMatchInput? match,
        StatisticsReadinessResult? readiness)
    {
        var gate = new RecommendationGate();
        variables ??= new List<ResearchVariable>();
        match ??= new StatisticsMatchInput();

        var named = variables.Where(v => !string.IsNullOrWhiteSpace(v.VariableName)).ToList();
        bool sheet = named.Count > 0;
        bool dataset = data is { RowCount: > 0, ColumnCount: > 0 };
        bool sampleComplete = readiness is null || !readiness.Issues.Any(i => i.Code == "SampleIncomplete");
        int matched = named.Count(v => match.VariableColumn.ContainsKey(v.Id));
        bool anyMatched = matched > 0;
        bool blockers = readiness is not null && readiness.State == StatisticsReadinessState.Blocked;
        bool descriptiveReady = readiness is not null && readiness.CanRun;

        // A usable outcome: role Outcome, matched, analyzable, with valid values.
        var outcome = FindOutcome(named, data, match);
        bool outcomeOk = outcome is not null;

        gate.Checklist.Add(new() { Label = "Extraction Sheet complete", Passed = sheet });
        gate.Checklist.Add(new() { Label = "Full CSV dataset loaded", Passed = dataset });
        gate.Checklist.Add(new() { Label = "Sample size complete", Passed = sampleComplete });
        gate.Checklist.Add(new() { Label = "Variables matched to dataset", Passed = anyMatched });
        gate.Checklist.Add(new() { Label = "Outcome variable selected", Passed = outcomeOk });
        gate.Checklist.Add(new() { Label = "Data quality blockers resolved", Passed = !blockers });
        gate.Checklist.Add(new() { Label = "Descriptive statistics ready", Passed = descriptiveReady });

        gate.Locked = !descriptiveReady || blockers || !sheet || !dataset || !sampleComplete || !anyMatched || !outcomeOk;
        gate.HasWarnings = readiness is not null && readiness.State == StatisticsReadinessState.NeedsReview;

        if (gate.Locked)
        {
            gate.PrimaryReason = !descriptiveReady || blockers
                ? "Descriptive Statistics readiness is not complete. Resolve the remaining items before planning inferential tests."
                : !outcomeOk
                    ? "No usable outcome variable was found. Mark the variable that answers your research question as the Outcome (and make sure it has data)."
                    : "Complete the Descriptive Statistics readiness checklist first.";
        }
        return gate;
    }

    // ---- Build the recommendations ------------------------------------------
    public static TestRecommendationResult Build(
        IReadOnlyList<ResearchVariable> variables,
        StatisticsDataset? data,
        StatisticsMatchInput? match,
        StatisticsReadinessResult? readiness)
    {
        var result = new TestRecommendationResult();
        variables ??= new List<ResearchVariable>();
        match ??= new StatisticsMatchInput();

        result.GlobalNotes.Add("This section recommends future inferential tests only. No p-values or inferential results are calculated in this phase.");
        result.GlobalNotes.Add("Recommendations are produced by deterministic rules from your Extraction Sheet and observed dataset structure — never by AI.");

        var named = variables.Where(v => !string.IsNullOrWhiteSpace(v.VariableName)).ToList();
        var outcome = FindOutcome(named, data, match);
        if (outcome is null || data is null)
        {
            result.GlobalNotes.Add("No usable outcome variable is available, so no test recommendations can be planned yet.");
            return result;
        }

        var (oPrep, oKind, oGroups) = Classify(outcome, data, match);
        result.OutcomeName = outcome.VariableName.Trim();
        result.OutcomeDisplay = Display(outcome);

        // Candidate predictors: every OTHER matched, named variable that is not
        // an identifier. Unsupported/ambiguous ones still get a card so the user
        // sees why they were excluded (never silently dropped).
        foreach (var v in named)
        {
            if (ReferenceEquals(v, outcome) || v.Id == outcome.Id) continue;
            if (!match.VariableColumn.TryGetValue(v.Id, out string? col) || data.ColumnIndexOf(col ?? "") < 0) continue;
            if (IsIdentifier(v)) continue;

            var (pPrep, pKind, pGroups) = Classify(v, data, match);
            var rec = BuildPair(outcome, oPrep, oKind, oGroups, v, pPrep, pKind, pGroups);
            ApplyPlanningNoteWording(rec);
            result.Recommendations.Add(rec);
        }

        // Stable, useful ordering: by status severity (ready first), then name.
        result.Recommendations = result.Recommendations
            .OrderBy(r => StatusOrder(r.Status))
            .ThenBy(r => r.PredictorName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return result;
    }

    private static int StatusOrder(TestRecoStatus s) => s switch
    {
        TestRecoStatus.Ready => 0,
        TestRecoStatus.NeedsAssumptionReview => 1,
        TestRecoStatus.NeedsRoleReview => 2,
        TestRecoStatus.NeedsMetadataReview => 3,
        TestRecoStatus.NotRecommended => 4,
        _ => 5
    };

    // ---- Core rule engine for one pair --------------------------------------
    private static TestRecommendation BuildPair(
        ResearchVariable outcome, VariablePreparation oPrep, RecoVarKind oKind, int oGroups,
        ResearchVariable predictor, VariablePreparation pPrep, RecoVarKind pKind, int pGroups)
    {
        var r = new TestRecommendation
        {
            OutcomeName = outcome.VariableName.Trim(), OutcomeDisplay = Display(outcome),
            PredictorName = predictor.VariableName.Trim(), PredictorDisplay = Display(predictor),
            OutcomeKind = KindDisplay(oKind), PredictorKind = KindDisplay(pKind),
            OutcomeGroups = oGroups, PredictorGroups = pGroups,
            OutcomeValidN = oPrep.ValidN, OutcomeMissingN = oPrep.MissingN,
            PredictorValidN = pPrep.ValidN, PredictorMissingN = pPrep.MissingN,
            PairTypeDisplay = $"{KindDisplay(oKind)} outcome vs {KindDisplay(pKind)} predictor"
        };
        r.PredictorRole = RoleDisplayFor(predictor, pKind);
        var roleClass = ClassifyRole(predictor.Role);
        r.Notes.Add("No p-value calculated in this phase.");
        r.Notes.Add("Planning only — this is a recommended future test, not a result.");

        // 1) Unsupported variable types (Text / Date / ID) — role-independent.
        if (oKind == RecoVarKind.Unsupported || pKind == RecoVarKind.Unsupported)
        {
            r.Status = TestRecoStatus.Unsupported;
            r.RecommendedTest = "No test recommended";
            string which = oKind == RecoVarKind.Unsupported ? "outcome" : "predictor";
            r.Rationale = $"The {which} variable's type is text, date, or an identifier, which is not suitable for this recommendation engine.";
            return r;
        }

        // 2) Ambiguous metadata / structural problems — role-independent.
        if (oKind == RecoVarKind.Ambiguous || pKind == RecoVarKind.Ambiguous)
        {
            r.Status = TestRecoStatus.NeedsMetadataReview;
            r.RecommendedTest = "Needs variable metadata review";
            r.Rationale = AmbiguityReason(oKind == RecoVarKind.Ambiguous ? oPrep : pPrep,
                                          oKind == RecoVarKind.Ambiguous ? "outcome" : "predictor",
                                          oKind == RecoVarKind.Ambiguous ? oGroups : pGroups);
            return r;
        }

        // 3) Too little data to plan a comparison — role-independent.
        if (oPrep.ValidN < MinValidObservations || pPrep.ValidN < MinValidObservations)
        {
            r.Status = TestRecoStatus.NotRecommended;
            r.RecommendedTest = "Not recommended (too few valid observations)";
            r.Rationale = $"There are too few valid observations to plan a comparison (need at least {MinValidObservations}).";
            return r;
        }

        bool oCat = oKind is RecoVarKind.Binary or RecoVarKind.Nominal;
        bool pCat = pKind is RecoVarKind.Binary or RecoVarKind.Nominal;

        // A. Categorical/binary outcome vs categorical/binary predictor → Chi-square.
        // Assumption-dependent (expected cell counts must be checked).
        if (oCat && pCat)
        {
            r.RecommendedTest = "Chi-square test of independence";
            bool twoByTwo = oKind == RecoVarKind.Binary && pKind == RecoVarKind.Binary;
            if (twoByTwo)
            {
                r.AlternativeTest = "Fisher exact test (for small expected counts)";
                r.Notes.Add("For a 2×2 table, a future effect size may include the odds ratio — the odds ratio is not calculated in this phase.");
            }
            else
            {
                r.Notes.Add("Fisher exact test may be needed if some expected cell counts are small.");
            }
            r.Rationale = "Both variables are categorical, so association is planned with a chi-square test of independence. Observations are assumed independent.";
            r.Checklist.Add("Independent observations: assumed");
            r.Checklist.Add($"At least 2 outcome categories: {(oGroups >= 2 ? "yes" : "no")}");
            r.Checklist.Add($"At least 2 predictor categories: {(pGroups >= 2 ? "yes" : "no")}");
            r.Checklist.Add("Expected cell counts: needs review before the final test");
            r.AssumptionDependent = true;
            Finalize(r, roleClass, oPrep, pPrep);
            return r;
        }

        // B. Continuous vs categorical/binary → t-test/ANOVA (assumption-dependent).
        if ((oKind == RecoVarKind.Continuous && pCat) || (pKind == RecoVarKind.Continuous && oCat))
        {
            var catKind = oKind == RecoVarKind.Continuous ? pKind : oKind;
            int groups = oKind == RecoVarKind.Continuous ? pGroups : oGroups;
            if (catKind == RecoVarKind.Binary || groups == 2)
            {
                r.RecommendedTest = "Independent-samples t-test";
                r.AlternativeTest = "Mann-Whitney U test (if not approximately normal)";
                r.Rationale = "A continuous measure is compared between 2 independent groups. Use a t-test when its assumptions hold, or the Mann-Whitney U test when the data are skewed or the assumptions are not met.";
                r.Checklist.Add("Independent groups: assumed");
                r.Checklist.Add("Continuous outcome: yes");
                r.Checklist.Add("Exactly 2 groups: yes");
                r.Checklist.Add("Approximately normal within groups: needs review");
                r.Checklist.Add("Similar variance across groups: needs review");
            }
            else
            {
                r.RecommendedTest = "One-way ANOVA";
                r.AlternativeTest = "Kruskal-Wallis test (if not approximately normal)";
                r.Rationale = $"A continuous measure is compared across {groups} independent groups. Use one-way ANOVA when its assumptions hold, or the Kruskal-Wallis test when they are not met.";
                r.Checklist.Add("Independent observations: assumed");
                r.Checklist.Add("Continuous outcome: yes");
                r.Checklist.Add($"3+ groups: yes ({groups})");
                r.Checklist.Add("Approximately normal within groups: needs review");
                r.Checklist.Add("Similar variance across groups: needs review");
            }
            r.AssumptionDependent = true;
            Finalize(r, roleClass, oPrep, pPrep);
            return r;
        }

        // C. Ordinal vs categorical/binary → non-parametric rank comparison.
        // NOT assumption-dependent (rank test; only basic independence assumed).
        if ((oKind == RecoVarKind.Ordinal && pCat) || (pKind == RecoVarKind.Ordinal && oCat))
        {
            int groups = oKind == RecoVarKind.Ordinal ? pGroups : oGroups;
            var catKind = oKind == RecoVarKind.Ordinal ? pKind : oKind;
            if (catKind == RecoVarKind.Binary || groups == 2)
            {
                r.RecommendedTest = "Mann-Whitney U test";
                r.Rationale = "An ordinal measure is compared between 2 groups, so a non-parametric rank test (Mann-Whitney U) is planned.";
                r.Checklist.Add("Independent groups: assumed");
                r.Checklist.Add("Ordinal outcome: yes");
                r.Checklist.Add("Exactly 2 groups: yes");
            }
            else
            {
                r.RecommendedTest = "Kruskal-Wallis test";
                r.Rationale = $"An ordinal measure is compared across {groups} groups, so a non-parametric rank test (Kruskal-Wallis) is planned.";
                r.Checklist.Add("Independent observations: assumed");
                r.Checklist.Add("Ordinal outcome: yes");
                r.Checklist.Add($"3+ groups: yes ({groups})");
            }
            r.AssumptionDependent = false;
            Finalize(r, roleClass, oPrep, pPrep);
            return r;
        }

        // D. Continuous vs continuous → correlation (assumption-dependent).
        if (oKind == RecoVarKind.Continuous && pKind == RecoVarKind.Continuous)
        {
            r.RecommendedTest = "Pearson correlation";
            r.AlternativeTest = "Spearman correlation (if non-linear/skewed)";
            r.Rationale = "Two continuous measures are related, so a correlation is planned. Use Pearson for an approximately linear, normal relationship, or Spearman for a monotonic/skewed one.";
            r.Checklist.Add("Both variables continuous: yes");
            r.Checklist.Add("Approximately linear relationship: needs review");
            r.Checklist.Add("Approximately normal: needs review (Pearson)");
            r.Checklist.Add("No coefficient calculated in this phase");
            r.Notes.Add("No correlation coefficient is calculated in this phase.");
            r.AssumptionDependent = true;
            Finalize(r, roleClass, oPrep, pPrep);
            return r;
        }

        // E. Ordinal ↔ continuous, or ordinal ↔ ordinal → Spearman
        // (assumption-dependent: a monotonic relationship is only assumed).
        if ((oKind == RecoVarKind.Ordinal || oKind == RecoVarKind.Continuous)
            && (pKind == RecoVarKind.Ordinal || pKind == RecoVarKind.Continuous))
        {
            bool bothOrdinal = oKind == RecoVarKind.Ordinal && pKind == RecoVarKind.Ordinal;
            r.RecommendedTest = "Spearman correlation";
            r.AlternativeTest = bothOrdinal ? "an ordinal association measure" : "";
            r.Rationale = bothOrdinal
                ? "Both variables are ordinal, so a rank-based (Spearman) association may be appropriate."
                : "One variable is ordinal and the other continuous, so a rank-based (Spearman) association may be appropriate.";
            r.Checklist.Add("At least one ordinal variable: yes");
            r.Checklist.Add("Monotonic relationship: needs review");
            r.Checklist.Add("Independent observations: assumed");
            r.Checklist.Add("No coefficient calculated in this phase");
            r.Notes.Add("No correlation coefficient is calculated in this phase.");
            r.AssumptionDependent = true;
            Finalize(r, roleClass, oPrep, pPrep);
            return r;
        }

        // Fallback — should be unreachable, but never guess.
        r.Status = TestRecoStatus.NeedsMetadataReview;
        r.RecommendedTest = "Needs variable metadata review";
        r.Rationale = "The combination of variable types could not be matched to a safe recommendation. Review the variable types and roles in the Extraction Sheet.";
        return r;
    }

    // Phase 4B Part 2 wording fix (UI copy only — no math/eligibility change): a
    // card that CAN actually be computed (CanComputeCategorical / CanComputeRank /
    // CanComputeSpearman are unaffected, computed purely from Status + Kind) used
    // to say "No p-value calculated in this phase." / "No correlation coefficient
    // is calculated in this phase." — read as a permanent limitation even though
    // clicking Run computes a real result. Swap in wording that points at Run
    // instead. Cards that truly cannot be computed (role review / unsupported /
    // not recommended) keep the original wording unchanged.
    private static void ApplyPlanningNoteWording(TestRecommendation r)
    {
        if (!(r.CanComputeCategorical || r.CanComputeRank || r.CanComputeSpearman || r.CanComputeWelch || r.CanComputeAnova || r.CanComputePearson)) return;
        const string RunNote = "Planning card only — no result has been calculated yet. Click Run this analysis to compute the supported test locally.";
        bool replaced = false;
        for (int i = r.Notes.Count - 1; i >= 0; i--)
        {
            if (r.Notes[i] != "No p-value calculated in this phase." && r.Notes[i] != "No correlation coefficient is calculated in this phase.")
                continue;
            if (replaced) { r.Notes.RemoveAt(i); continue; }   // avoid a duplicate note on cards that had both
            r.Notes[i] = RunNote;
            replaced = true;
        }
    }

    // Turns a planned recommendation into a final status using role-awareness
    // and assumption dependence:
    //   * an unclear/blank role or an outcome-like role → Needs role review;
    //   * a clear predictor role + an assumption-dependent test → Needs assumption review;
    //   * a clear predictor role + a robust (rank) test → Ready.
    // High missingness downgrades an otherwise-Ready item to assumption review.
    private static void Finalize(TestRecommendation r, RecoRoleClass role, VariablePreparation oPrep, VariablePreparation pPrep)
    {
        if (role == RecoRoleClass.OutcomeLike)
        {
            r.Status = TestRecoStatus.NeedsRoleReview;
            r.Notes.Insert(0, "This variable is marked as an outcome-like variable. Confirm whether it should be analyzed as a predictor or a secondary outcome.");
        }
        else if (role == RecoRoleClass.Unclear)
        {
            r.Status = TestRecoStatus.NeedsRoleReview;
            r.Notes.Insert(0, "Review this variable's role before treating it as a predictor.");
        }
        else
        {
            r.Status = r.AssumptionDependent ? TestRecoStatus.NeedsAssumptionReview : TestRecoStatus.Ready;
        }
        AddMissingnessNote(r, oPrep, pPrep);
    }

    private static RecoRoleClass ClassifyRole(string? role)
    {
        string r = (role ?? "").Trim().ToLowerInvariant();
        if (r is "identifier" or "id" or "metadata") return RecoRoleClass.Excluded;
        if (r is "outcome" or "secondary outcome" or "dependent variable" or "dependent") return RecoRoleClass.OutcomeLike;
        if (r is "predictor" or "exposure" or "group" or "independent variable" or "independent"
              or "risk factor" or "confounder" or "covariate") return RecoRoleClass.Primary;
        return RecoRoleClass.Unclear;   // other / unknown / demographic / eligibility / blank
    }

    private static string RoleDisplayFor(ResearchVariable v, RecoVarKind kind)
    {
        if (kind == RecoVarKind.Unsupported) return "Unsupported";
        string r = (v.Role ?? "").Trim();
        return r.Length == 0 || r.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? "Unknown role" : r;
    }

    private static void AddMissingnessNote(TestRecommendation r, VariablePreparation o, VariablePreparation p)
    {
        if (o.MissingPercent >= HighMissingPercent || p.MissingPercent >= HighMissingPercent)
        {
            r.Notes.Add($"High missingness detected (outcome {o.MissingPercent:F0}%, predictor {p.MissingPercent:F0}%). Review completeness before relying on the future test.");
            if (r.Status == TestRecoStatus.Ready) r.Status = TestRecoStatus.NeedsAssumptionReview;
        }
    }

    private static string AmbiguityReason(VariablePreparation prep, string role, int groups)
    {
        if (groups <= 1)
            return $"The {role} variable has only one observed category in the dataset — there is nothing to compare.";
        if (prep.ValidN == 0)
            return $"The {role} variable has no valid values in the dataset.";
        return $"The {role} variable's type or measurement level is unclear. Set a specific type and level in the Extraction Sheet.";
    }

    // ---- Classification ------------------------------------------------------
    private static (VariablePreparation Prep, RecoVarKind Kind, int Groups) Classify(
        ResearchVariable v, StatisticsDataset? data, StatisticsMatchInput match)
    {
        match.VariableColumn.TryGetValue(v.Id, out string? col);
        var prep = StatisticsVariablePreparer.Prepare(v, data, col);
        int groups = prep.DistinctMeaningful.Count;

        string type = (v.VariableType ?? "").Trim();
        string level = (v.MeasurementLevel ?? "").Trim();

        if (IsIdentifier(v) || type.Equals("Text", StringComparison.OrdinalIgnoreCase) || type.Equals("Date", StringComparison.OrdinalIgnoreCase))
            return (prep, RecoVarKind.Unsupported, groups);

        if (prep.Kind == VariableAnalysisKind.Excluded || type.Length == 0 || type.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            return (prep, RecoVarKind.Ambiguous, groups);

        if (prep.ValidN == 0 || groups <= 1)
            return (prep, RecoVarKind.Ambiguous, groups);

        if (prep.Kind == VariableAnalysisKind.Continuous)
            return (prep, RecoVarKind.Continuous, groups);

        // Frequencies family: ordinal, or binary/nominal by observed group count.
        if (prep.IsOrdinal || type.Equals("Ordinal", StringComparison.OrdinalIgnoreCase))
            return (prep, RecoVarKind.Ordinal, groups);

        return (prep, groups == 2 ? RecoVarKind.Binary : RecoVarKind.Nominal, groups);
    }

    private static bool IsIdentifier(ResearchVariable v)
        => (v.Role ?? "").Trim().Equals("Identifier", StringComparison.OrdinalIgnoreCase)
           || (v.VariableType ?? "").Trim().Equals("ID", StringComparison.OrdinalIgnoreCase);

    private static ResearchVariable? FindOutcome(IReadOnlyList<ResearchVariable> named, StatisticsDataset? data, StatisticsMatchInput match)
    {
        foreach (var v in named.Where(v => (v.Role ?? "").Trim().Equals("Outcome", StringComparison.OrdinalIgnoreCase)))
        {
            if (!match.VariableColumn.TryGetValue(v.Id, out string? col) || data is null || data.ColumnIndexOf(col ?? "") < 0) continue;
            var (_, kind, _) = Classify(v, data, match);
            if (kind is not (RecoVarKind.Unsupported or RecoVarKind.Ambiguous)) return v;
        }
        return null;
    }

    private static string KindDisplay(RecoVarKind k) => k switch
    {
        RecoVarKind.Continuous => "Continuous",
        RecoVarKind.Binary => "Binary",
        RecoVarKind.Nominal => "Categorical",
        RecoVarKind.Ordinal => "Ordinal",
        RecoVarKind.Unsupported => "Unsupported",
        _ => "Unclear"
    };

    // Professional display name (same policy as descriptive statistics):
    //   1) a distinct Extraction-Sheet label → "name (label)";
    //   2) an ugly underscored name with no label → prettified to a readable
    //      phrase (underscores → spaces, sentence case, trailing "?" for a
    //      yes/no-question-shaped name);
    //   3) an already-clean name → shown as-is.
    // The stored VariableName is never modified — display only.
    private static string Display(ResearchVariable v)
    {
        string name = v.VariableName.Trim();
        string label = (v.QuestionLabel ?? "").Trim();
        if (label.Length > 0 && !string.Equals(label, name, StringComparison.OrdinalIgnoreCase))
            return $"{name} ({Shorten(label, 60)})";
        return name.Contains('_') ? Shorten(PrettifyIdentifier(name), 70) : name;
    }

    private static readonly string[] QuestionStarters =
    {
        "do ", "does ", "did ", "is ", "are ", "was ", "were ", "has ", "have ", "had ",
        "can ", "could ", "will ", "would ", "should "
    };

    private static string PrettifyIdentifier(string name)
    {
        string s = (name ?? "").Trim().Replace('_', ' ');
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        if (s.Length == 0) return s;
        s = char.ToUpperInvariant(s[0]) + s.Substring(1);
        string lower = s.ToLowerInvariant();
        if (QuestionStarters.Any(q => lower.StartsWith(q)) && !s.EndsWith("?") && !s.EndsWith("."))
            s += "?";
        return s;
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s.Substring(0, max).TrimEnd() + "…";
}

// ---------------------------------------------------------------------------
// Export: plain text and CSV of the recommendation plan. Pure string builders;
// no participant-level data, no calculated statistics.
// ---------------------------------------------------------------------------
public static class TestRecommendationExport
{
    public static string BuildPlainText(TestRecommendationResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RECOMMENDED ANALYSIS — STATISTICAL TEST PLAN");
        sb.AppendLine($"Generated: {r.GeneratedDisplay}");
        sb.AppendLine($"Outcome variable: {r.OutcomeDisplay}");
        sb.AppendLine($"Candidate variables: {r.CandidatePredictors}");
        sb.AppendLine($"  Ready to plan: {r.ReadyCount}");
        sb.AppendLine($"  Needs assumption review: {r.AssumptionReviewCount}");
        sb.AppendLine($"  Needs role review: {r.RoleReviewCount}");
        sb.AppendLine($"  Unsupported / not recommended: {r.UnsupportedCount}");
        sb.AppendLine("Planning only. No p-values or inferential results are calculated.");
        sb.AppendLine(new string('=', 78));
        foreach (var n in r.GlobalNotes) sb.AppendLine("• " + n);

        // Grouped by status so the plan reads as clearly-sorted sections.
        foreach (var group in r.Recommendations.GroupBy(x => x.GroupDisplay).OrderBy(g => g.First().GroupOrder))
        {
            sb.AppendLine();
            sb.AppendLine($"### {group.Key.ToUpperInvariant()} ({group.Count()})");
            foreach (var rec in group)
            {
                sb.AppendLine();
                sb.AppendLine(new string('-', 78));
                sb.AppendLine($"Predictor: {rec.PredictorDisplay}");
                sb.AppendLine($"Role:      {rec.PredictorRole}");
                sb.AppendLine($"Pairing:   {rec.PairTypeDisplay}");
                sb.AppendLine($"Groups:    outcome {rec.OutcomeGroups}, predictor {rec.PredictorGroups}   |   Valid N: outcome {rec.OutcomeValidN} (missing {rec.OutcomeMissingN}), predictor {rec.PredictorValidN} (missing {rec.PredictorMissingN})");
                sb.AppendLine($"{rec.PlanningLabel}: {rec.TestDisplay}");
                sb.AppendLine($"Why: {rec.Rationale}");
                if (rec.Checklist.Count > 0)
                {
                    sb.AppendLine("Checklist:");
                    foreach (var c in rec.Checklist) sb.AppendLine("   - " + c);
                }
                sb.AppendLine($"Status: {rec.StatusDisplay}");
                foreach (var n in rec.Notes) sb.AppendLine("Note: " + n);
            }
        }
        return sb.ToString();
    }

    public static string BuildCsv(TestRecommendationResult r)
    {
        var sb = new StringBuilder();
        string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        sb.AppendLine(string.Join(",", new[]
        {
            "Group", "Outcome", "Predictor", "Role", "Pairing", "OutcomeGroups", "PredictorGroups",
            "OutcomeValidN", "OutcomeMissingN", "PredictorValidN", "PredictorMissingN",
            "RecommendedTest", "AlternativeTest", "Status", "Rationale", "PValueThisPhase"
        }.Select(Q)));
        foreach (var rec in r.Recommendations.OrderBy(x => x.GroupOrder).ThenBy(x => x.PredictorName, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(string.Join(",", new[]
            {
                Q(rec.GroupDisplay), Q(rec.OutcomeDisplay), Q(rec.PredictorDisplay), Q(rec.PredictorRole), Q(rec.PairTypeDisplay),
                rec.OutcomeGroups.ToString(CultureInfo.InvariantCulture), rec.PredictorGroups.ToString(CultureInfo.InvariantCulture),
                rec.OutcomeValidN.ToString(CultureInfo.InvariantCulture), rec.OutcomeMissingN.ToString(CultureInfo.InvariantCulture),
                rec.PredictorValidN.ToString(CultureInfo.InvariantCulture), rec.PredictorMissingN.ToString(CultureInfo.InvariantCulture),
                Q(rec.RecommendedTest), Q(rec.AlternativeTest), Q(rec.StatusDisplay), Q(rec.Rationale), Q("Not calculated")
            }));
        }
        return sb.ToString();
    }
}
