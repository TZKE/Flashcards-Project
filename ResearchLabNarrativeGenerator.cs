using System.Text;

namespace AIFlashcardMaker;

// ===========================================================================
// Research Lab — Phase 4F (Slices 1-2): deterministic Methods/Results narrative
// generator (BACKEND / HEADLESS ONLY).
//
// Turns an already-computed, in-memory inference result (an IInferenceExportable)
// into copy-ready academic "Methods" and "Results" prose. Slice 1 shipped a RICH
// template for the 2×2 measures result (odds ratio + design-gated risk/prevalence
// ratio & difference with 95% CIs). Slice 2 adds RICH templates for the remaining
// computed result types — Welch t-test, one-way ANOVA, Pearson correlation,
// Spearman rank correlation, the rank tests (Mann-Whitney U and Kruskal-Wallis),
// and the categorical association tests (chi-square and Fisher exact) — with a
// conservative GENERIC fallback for anything not yet templated.
//
// HARD RULES (audit-critical):
//   * Deterministic C# only. Same result object → byte-identical text bodies.
//     The ONLY non-deterministic field is GeneratedAt (a wall-clock stamp on the
//     DTO); no timestamp is ever embedded in the narrative text itself.
//   * NO AI, NO HTTP, NO randomness, NO file I/O, NO logging, NO clipboard, NO
//     persistence, NO WPF. This class only READS public aggregate properties off
//     the result object and formats them — it never recomputes any statistic.
//   * Reuses the existing formatters (InferenceMath.FormatNumber / FormatPValue
//     and TwoByTwoMeasuresResult.CiText). No new math beyond string assembly.
//   * Only aggregate values (counts, effect estimates, p-values, CIs, variable
//     and level labels) reach the output. NO participant rows, NO row indices,
//     NO identifiers — nothing row-level exists on the result objects to leak.
//   * Association wording only — never causation. Crude/unadjusted is always
//     stated for 2×2. Risk vs prevalence wording is driven by the engine's own
//     labels so it can never contradict the design gate.
//   * ISOLATED — no existing type is modified; this is a brand-new file with a
//     static generator plus its output DTO. Wiring into any UI is a later slice.
// ===========================================================================

// Deterministic, AI-free narrative output. Plain strings + a couple of flags.
public sealed class ResearchLabNarrativeResult
{
    public string Title { get; set; } = "";
    public string MethodsText { get; set; } = "";
    public string ResultsText { get; set; } = "";
    public string NotesText { get; set; } = "";

    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public string SourceResultTitle { get; set; } = "";

    // Invariants for this generator: always deterministic, never AI-assisted.
    public bool IsDeterministic { get; set; } = true;
    public bool AiUsed { get; set; } = false;

    // Methods + Results + Notes, composed cleanly with section headings. Uses "\n"
    // only (no wall-clock content) so it is as deterministic as its parts.
    public string MethodsPlusResults
    {
        get
        {
            var parts = new List<string>();
            void Add(string heading, string body)
            {
                if (!string.IsNullOrWhiteSpace(body))
                    parts.Add(heading + "\n\n" + body.Trim());
            }
            Add("Methods", MethodsText);
            Add("Results", ResultsText);
            Add("Notes", NotesText);
            return string.Join("\n\n", parts);
        }
    }
}

public static class ResearchLabNarrativeGenerator
{
    // Shared stale banner (section 11) — identical wording wherever it appears.
    private const string StaleWarning =
        "This result is marked stale because the dataset or extraction sheet changed after it was computed. "
        + "Re-run the analysis before using this text in a manuscript.";

    // Shared privacy/determinism footer sentence (section 12).
    private const string DeterministicFooter =
        "Generated deterministically from aggregate computed results on this device. No AI was used.";
    private const string NoRowsFooter =
        "No participant-level rows or identifiers are included in this narrative.";

    // ---------------------------------------------------------------------
    // Public API. Never throws for null / unsupported / malformed results.
    // ---------------------------------------------------------------------
    public static ResearchLabNarrativeResult Generate(IInferenceExportable? result, bool isStale = false)
    {
        try
        {
            if (result is null) return BuildNullResult(isStale);

            if (result is TwoByTwoMeasuresResult two)
            {
                return two.Status switch
                {
                    TwoByTwoStatus.Computed          => BuildTwoByTwoComputed(two, isStale),
                    TwoByTwoStatus.NeedsLevelReview  => BuildTwoByTwoNeedsLevelReview(two, isStale),
                    _                                => BuildTwoByTwoNotComputed(two, isStale), // CannotCompute | NotRunnable
                };
            }

            if (result is WelchTTestResult welch)
                return welch.Status == ParametricStatus.Computed
                    ? BuildWelchComputed(welch, isStale)
                    : BuildBlocked(CleanName(welch.TestUsed, "Welch independent-samples t-test"), welch.ResultTitle,
                        $"{Safe(welch.OutcomeDisplay, "a continuous outcome")} by {Safe(welch.GroupingDisplay, "a two-group variable")}",
                        welch.Status == ParametricStatus.NotRunnable, welch.StatusReason, isStale);

            if (result is OneWayAnovaResult anova)
                return anova.Status == ParametricStatus.Computed
                    ? BuildAnovaComputed(anova, isStale)
                    : BuildBlocked(CleanName(anova.TestUsed, "one-way ANOVA"), anova.ResultTitle,
                        $"{Safe(anova.OutcomeDisplay, "a continuous outcome")} by {Safe(anova.GroupingDisplay, "a grouping variable")}",
                        anova.Status == ParametricStatus.NotRunnable, anova.StatusReason, isStale);

            if (result is PearsonCorrelationResult pearson)
                return pearson.Status == ParametricStatus.Computed
                    ? BuildPearsonComputed(pearson, isStale)
                    : BuildBlocked(CleanName(pearson.TestUsed, "Pearson correlation"), pearson.ResultTitle,
                        $"{Safe(pearson.XDisplay, "one variable")} and {Safe(pearson.YDisplay, "another variable")}",
                        pearson.Status == ParametricStatus.NotRunnable, pearson.StatusReason, isStale);

            if (result is SpearmanResult spearman)
                return spearman.Status == SpearmanStatus.Computed
                    ? BuildSpearmanComputed(spearman, isStale)
                    : BuildBlocked(CleanName(spearman.TestUsed, "Spearman rank correlation"), spearman.ResultTitle,
                        $"{Safe(spearman.XDisplay, "one variable")} and {Safe(spearman.YDisplay, "another variable")}",
                        spearman.Status == SpearmanStatus.NotRunnable, spearman.StatusReason, isStale);

            if (result is RankTestResult rank)
            {
                if (rank.Status != RankTestStatus.Computed)
                    return BuildBlocked(RankTestName(rank), rank.ResultTitle,
                        $"{Safe(rank.RankedDisplay, "a ranked outcome")} by {Safe(rank.GroupingDisplay, "a grouping variable")}",
                        rank.Status == RankTestStatus.NotRunnable, rank.StatusReason, isStale);
                return IsKruskalWallis(rank)
                    ? BuildKruskalWallisComputed(rank, isStale)
                    : BuildMannWhitneyComputed(rank, isStale);
            }

            if (result is CategoricalTestResult cat)
                return cat.Status == CategoricalTestStatus.Computed
                    ? BuildCategoricalComputed(cat, isStale)
                    : BuildBlocked("contingency-table association test", cat.ResultTitle,
                        $"{Safe(cat.OutcomeDisplay, "one categorical variable")} and {Safe(cat.PredictorDisplay, "another categorical variable")}",
                        cat.Status == CategoricalTestStatus.NotRunnable, cat.StatusReason, isStale);

            return BuildGenericFallback(result, isStale);
        }
        catch
        {
            // Defensive: a narrative generator must never crash the caller.
            return BuildSafeError(result, isStale);
        }
    }

    // =====================================================================
    // Rich 2×2 template — COMPUTED.
    // =====================================================================
    private static ResearchLabNarrativeResult BuildTwoByTwoComputed(TwoByTwoMeasuresResult r, bool isStale)
    {
        string outcome = Safe(r.OutcomeDisplay, "the outcome");
        string exposure = Safe(r.ExposureDisplay, "the exposure");
        string eventLevel = Safe(r.EventLevelDisplay, "the event-positive level");
        string exposedLevel = Safe(r.ExposedLevelDisplay, "the exposure-positive level");

        // ---- Methods ----
        var m = new StringBuilder();
        m.Append($"Associations between {exposure} and {outcome} were summarized using a 2×2 contingency table, ");
        m.Append($"with {eventLevel} treated as the event-positive outcome and {exposedLevel} treated as the exposure-positive level. ");
        m.Append("The odds ratio (OR) was reported as the primary effect measure, and its 95% confidence interval was calculated using the log (Woolf) method. ");
        if (r.AssociationP is not null)
            m.Append("The two-sided Fisher exact test was used to obtain the association p-value. ");
        else
            m.Append("An association p-value from the Fisher exact test was not available for this table. ");

        if (r.AreRiskMeasuresReported)
        {
            string ratioLabel = LowerFirstWord(Safe(r.RatioLabel, "Risk ratio"));
            string diffLabel = LowerFirstWord(Safe(r.DifferenceLabel, "Risk difference"));
            m.Append($"Because the study design was classified as {DesignPhrase(r.StudyDesignKind)}, the {ratioLabel} and {diffLabel} were also reported, ");
            m.Append("each with a 95% confidence interval (log method for the ratio; approximate Wald method for the difference). ");
        }
        else if (r.SuppressionReason.Length > 0)
        {
            m.Append(r.SuppressionReason.Trim() + " ");
        }

        if (r.CorrectionApplied)
            m.Append("Because a zero cell was present, the Haldane-Anscombe correction (+0.5 to each cell) was applied to the affected ratio estimates and their intervals; the raw counts are reported unchanged. ");

        m.Append("All estimates are crude (unadjusted) and were not adjusted for confounders.");

        // ---- Results ----
        var res = new StringBuilder();
        res.Append($"A total of {r.N} complete participant pair(s) were analyzed");
        if (r.DroppedForMissing > 0) res.Append($" (excluded for missing values: {r.DroppedForMissing})");
        res.Append(". ");
        res.Append($"The event ({eventLevel}) occurred in {r.A} of {r.A + r.B} exposed participants ({exposedLevel}) ");
        res.Append($"and in {r.C} of {r.C + r.D} unexposed participants. ");
        res.Append($"The odds ratio was {r.OrDisplay} (95% CI {TwoByTwoMeasuresResult.CiText(r.OddsRatioCiLower, r.OddsRatioCiUpper)})");
        if (r.CorrectionApplied) res.Append(", Haldane-Anscombe corrected");
        res.Append(". ");
        if (r.AssociationP is not null)
        {
            res.Append($"The two-sided Fisher exact test gave p = {r.PValueDisplay}. ");
            res.Append(r.AssociationP.Value < 0.05
                ? "This association was statistically significant at the conventional 0.05 significance level. "
                : "This association did not reach statistical significance at the conventional 0.05 significance level (a non-significant result does not, by itself, establish that the two variables are unrelated). ");
        }
        if (r.AreRiskMeasuresReported)
        {
            string ratioLabel = LowerFirstWord(Safe(r.RatioLabel, "Risk ratio"));
            string diffLabel = LowerFirstWord(Safe(r.DifferenceLabel, "Risk difference"));
            res.Append($"The {ratioLabel} was {InferenceMath.FormatNumber(r.RatioMeasure, 3)} (95% CI {TwoByTwoMeasuresResult.CiText(r.RatioCiLower, r.RatioCiUpper)}), ");
            res.Append($"and the {diffLabel} was {InferenceMath.FormatNumber(r.DifferenceMeasure, 3)} (95% CI {TwoByTwoMeasuresResult.CiText(r.DifferenceCiLower, r.DifferenceCiUpper)}). ");
        }
        else if (r.SuppressionReason.Length > 0)
        {
            res.Append(r.SuppressionReason.Trim() + " ");
        }
        res.Append("These results describe an association and do not imply causation.");

        // ---- Notes ----
        var n = new StringBuilder();
        n.Append(DeterministicFooter + " ");
        n.Append("All estimates are crude (unadjusted) and were not adjusted for confounders. ");
        n.Append("These measures describe statistical association only; association does not imply causation. ");
        n.Append("The 95% confidence interval for the odds ratio uses the log (Woolf) method (z = 1.96)");
        if (r.AreRiskMeasuresReported)
            n.Append("; ratio intervals use the log method and the difference interval uses the approximate Wald method, which can extend beyond the -1 to +1 range with sparse data");
        n.Append(". Confidence intervals are approximate. ");
        if (r.StudyDesignKind == TwoByTwoStudyDesignKind.CrossSectional)
            n.Append("Cross-sectional data support prevalence measures. ");
        if (r.CorrectionApplied)
            n.Append("A zero cell was present, so sparse-data estimates should be interpreted with caution. ");
        if (isStale) n.Append(StaleWarning + " ");
        n.Append(NoRowsFooter);

        return new ResearchLabNarrativeResult
        {
            Title = $"Methods and Results — {outcome} × {exposure}",
            SourceResultTitle = r.ResultTitle,
            MethodsText = m.ToString().Trim(),
            ResultsText = res.ToString().Trim(),
            NotesText = n.ToString().Trim(),
        };
    }

    // =====================================================================
    // 2×2 — NEEDS LEVEL REVIEW (positive direction unresolved).
    // =====================================================================
    private static ResearchLabNarrativeResult BuildTwoByTwoNeedsLevelReview(TwoByTwoMeasuresResult r, bool isStale)
    {
        string outcome = Safe(r.OutcomeDisplay, "the outcome");
        string exposure = Safe(r.ExposureDisplay, "the exposure");

        string methods =
            $"A 2×2 association analysis was requested for {outcome} and {exposure}, but a Methods and Results narrative cannot be generated yet. "
            + "The event-positive level and the exposure-positive level could not be resolved automatically, "
            + "so no odds ratio, risk ratio, risk difference, or confidence interval was calculated.";

        var res = new StringBuilder();
        res.Append("No numeric results are available until the event level and the exposed level are clear. ");
        if (r.StatusReason.Length > 0) res.Append(r.StatusReason.Trim());

        var n = new StringBuilder();
        n.Append("Define the coding or value labels for each variable — for example, 1 = Yes and 0 = No — so the event-positive and exposure-positive levels are unambiguous, then re-run the analysis. ");
        n.Append(DeterministicFooter + " ");
        if (isStale) n.Append(StaleWarning + " ");
        n.Append(NoRowsFooter);

        return new ResearchLabNarrativeResult
        {
            Title = $"Methods and Results — {outcome} × {exposure} (needs level review)",
            SourceResultTitle = r.ResultTitle,
            MethodsText = methods,
            ResultsText = res.ToString().Trim(),
            NotesText = n.ToString().Trim(),
        };
    }

    // =====================================================================
    // 2×2 — CANNOT COMPUTE / NOT RUNNABLE (no valid result).
    // =====================================================================
    private static ResearchLabNarrativeResult BuildTwoByTwoNotComputed(TwoByTwoMeasuresResult r, bool isStale)
    {
        string outcome = Safe(r.OutcomeDisplay, "the outcome");
        string exposure = Safe(r.ExposureDisplay, "the exposure");

        string methods =
            $"A 2×2 association analysis was requested for {outcome} and {exposure}, "
            + "but no statistical narrative is available because the result was not computed.";

        var res = new StringBuilder();
        res.Append("No statistical results were produced for this pairing. ");
        if (r.StatusReason.Length > 0) res.Append(r.StatusReason.Trim() + " ");
        res.Append("No odds ratio, risk ratio, risk difference, or confidence interval was calculated.");

        var n = new StringBuilder();
        n.Append("Resolve the reason above and re-run the analysis to obtain a statistical narrative. ");
        n.Append(DeterministicFooter + " ");
        if (isStale) n.Append(StaleWarning + " ");
        n.Append(NoRowsFooter);

        return new ResearchLabNarrativeResult
        {
            Title = $"Methods and Results — {outcome} × {exposure} (not computed)",
            SourceResultTitle = r.ResultTitle,
            MethodsText = methods,
            ResultsText = res.ToString().Trim(),
            NotesText = n.ToString().Trim(),
        };
    }

    // =====================================================================
    // Welch independent-samples t-test — COMPUTED.
    // =====================================================================
    private static ResearchLabNarrativeResult BuildWelchComputed(WelchTTestResult r, bool isStale)
    {
        string outcome = Safe(r.OutcomeDisplay, "the outcome");
        string grouping = Safe(r.GroupingDisplay, "the grouping variable");

        var m = new StringBuilder();
        m.Append($"A continuous outcome ({outcome}) was compared between the two independent groups defined by {grouping} ");
        m.Append("using Welch's independent-samples t-test, which does not assume equal group variances. ");
        m.Append("The degrees of freedom followed the Welch-Satterthwaite approximation. ");
        m.Append("The calculation was performed locally by deterministic code.");

        var res = new StringBuilder();
        res.Append($"A total of {r.ValidN} participant(s) were analyzed{MissingSuffix(r.DroppedForMissing, r.DroppedInvalid)}. ");
        if (r.Groups.Count > 0)
            res.Append($"The groups were {WelchGroupsText(r.Groups)}. ");
        if (r.MeanDifference is not null)
            res.Append($"The mean difference was {InferenceMath.FormatNumber(r.MeanDifference, 3)}. ");
        res.Append($"Welch's t was {InferenceMath.FormatNumber(r.TStatistic, 3)} with {InferenceMath.FormatNumber(r.DegreesOfFreedom, 2)} degrees of freedom, ");
        res.Append($"and the two-sided p-value was {r.PValueDisplay}. ");
        res.Append(SignificanceSentence(r.PValue, $"The difference in {outcome} between the two groups", "a difference between the groups"));
        if (r.HedgesG is not null)
        {
            res.Append($"The standardized effect size (Hedges g) was {InferenceMath.FormatNumber(r.HedgesG, 3)}");
            if (r.CohensD is not null) res.Append($", and Cohen's d was {InferenceMath.FormatNumber(r.CohensD, 3)}");
            if (r.StrengthBand.Length > 0) res.Append($", corresponding to a {r.StrengthBand}");
            res.Append(". ");
        }
        res.Append("These results describe an observed statistical difference between groups and do not imply causation.");

        var n = new StringBuilder();
        n.Append(DeterministicFooter + " ");
        n.Append("This is a crude (unadjusted) comparison of two groups and was not adjusted for confounders. ");
        n.Append("An observed difference between groups does not imply causation. ");
        n.Append("Listed assumptions are informational and were not formally tested here. ");
        AppendNonSignificantCaution(n, r.PValue);
        if (r.StrengthBand.Length > 0) n.Append(HeuristicBandNote);
        if (isStale) n.Append(StaleWarning + " ");
        n.Append(NoRowsFooter);

        return Compose($"Methods and Results — {outcome} by {grouping}", r.ResultTitle, m, res, n);
    }

    // =====================================================================
    // One-way ANOVA — COMPUTED.
    // =====================================================================
    private static ResearchLabNarrativeResult BuildAnovaComputed(OneWayAnovaResult r, bool isStale)
    {
        string outcome = Safe(r.OutcomeDisplay, "the outcome");
        string grouping = Safe(r.GroupingDisplay, "the grouping variable");
        int groups = r.GroupCount > 0 ? r.GroupCount : r.Groups.Count;

        var m = new StringBuilder();
        m.Append($"A continuous outcome ({outcome}) was compared across the {groups} independent groups defined by {grouping} ");
        m.Append("using a one-way analysis of variance (one-way ANOVA). ");
        m.Append("The calculation was performed locally by deterministic code.");

        var res = new StringBuilder();
        res.Append($"A total of {r.ValidN} participant(s) across {groups} group(s) were analyzed{MissingSuffix(r.DroppedForMissing, r.DroppedInvalid)}. ");
        if (r.Groups.Count > 0)
            res.Append($"The group summaries were {AnovaGroupsText(r.Groups)}. ");
        res.Append($"The ANOVA F statistic was {InferenceMath.FormatNumber(r.FStatistic, 3)} with {r.DfBetween} and {r.DfWithin} degrees of freedom (between and within groups), ");
        res.Append($"and the p-value was {r.PValueDisplay}. ");
        res.Append(SignificanceSentence(r.PValue, "The difference among the group means", "a difference among the group means"));
        if (r.EtaSquared is not null)
        {
            res.Append($"The effect size (eta-squared) was {InferenceMath.FormatNumber(r.EtaSquared, 3)}");
            if (r.OmegaSquared is not null) res.Append($", and omega-squared was {InferenceMath.FormatNumber(r.OmegaSquared, 3)}");
            if (r.StrengthBand.Length > 0) res.Append($", corresponding to a {r.StrengthBand}");
            res.Append(". ");
        }
        res.Append("A significant ANOVA indicates that at least one group mean differs from the others; it does not identify which groups differ. ");
        res.Append("These results describe observed group differences and do not imply causation.");

        var n = new StringBuilder();
        n.Append(DeterministicFooter + " ");
        n.Append("Post-hoc pairwise comparisons were not performed, so this analysis does not identify which specific groups differ. ");
        n.Append("This is a crude (unadjusted) comparison and was not adjusted for confounders. ");
        n.Append("Observed differences among groups do not imply causation. ");
        n.Append("Listed assumptions are informational and were not formally tested here. ");
        if (r.StrengthBand.Length > 0) n.Append(HeuristicBandNote);
        if (isStale) n.Append(StaleWarning + " ");
        n.Append(NoRowsFooter);

        return Compose($"Methods and Results — {outcome} by {grouping}", r.ResultTitle, m, res, n);
    }

    // =====================================================================
    // Pearson correlation — COMPUTED.
    // =====================================================================
    private static ResearchLabNarrativeResult BuildPearsonComputed(PearsonCorrelationResult r, bool isStale)
    {
        string x = Safe(r.XDisplay, "the first variable");
        string y = Safe(r.YDisplay, "the second variable");

        var m = new StringBuilder();
        m.Append($"The linear association between two continuous variables ({x} and {y}) was quantified using the Pearson correlation coefficient. ");
        m.Append("The calculation was performed locally by deterministic code.");

        var res = new StringBuilder();
        res.Append($"A total of {r.PairN} complete pair(s) were analyzed{MissingSuffix(r.DroppedForMissing, r.DroppedInvalid)}. ");
        res.Append($"Pearson's r was {r.RDisplay}");
        if (r.RSquared is not null) res.Append($" (r-squared = {InferenceMath.FormatNumber(r.RSquared, 3)})");
        res.Append(". ");
        if (r.TStatistic is not null)
            res.Append($"The test statistic was t = {InferenceMath.FormatNumber(r.TStatistic, 3)} with {r.DegreesOfFreedom} degrees of freedom, and the two-sided p-value was {r.PValueDisplay}. ");
        else
            res.Append($"The two-sided p-value was {r.PValueDisplay}. ");
        res.Append(SignificanceSentence(r.PValue, "The linear correlation", "a linear correlation"));
        if (r.StrengthBand.Length > 0) res.Append($"This corresponds to a {r.StrengthBand}. ");
        if (r.PerfectCorrelation)
            res.Append("The coefficient reached its maximum magnitude (a perfect correlation in this sample); interpret this cautiously, as it often reflects a very small sample or an exact relationship within the data. ");
        res.Append("A correlation quantifies association only and does not imply causation.");

        var n = new StringBuilder();
        n.Append(DeterministicFooter + " ");
        n.Append("Pearson's correlation assumes an approximately linear relationship and can miss non-linear patterns. ");
        n.Append("This is a crude (unadjusted) association and was not adjusted for confounders. ");
        n.Append("Correlation does not imply causation. ");
        AppendNonSignificantCaution(n, r.PValue);
        if (r.StrengthBand.Length > 0) n.Append(HeuristicBandNote);
        if (isStale) n.Append(StaleWarning + " ");
        n.Append(NoRowsFooter);

        return Compose($"Methods and Results — {x} vs {y}", r.ResultTitle, m, res, n);
    }

    // =====================================================================
    // Spearman rank correlation — COMPUTED.
    // =====================================================================
    private static ResearchLabNarrativeResult BuildSpearmanComputed(SpearmanResult r, bool isStale)
    {
        string x = Safe(r.XDisplay, "the first variable");
        string y = Safe(r.YDisplay, "the second variable");

        var m = new StringBuilder();
        m.Append($"The monotonic association between {x} and {y} was quantified using Spearman's rank correlation (rho), computed from the ranks of the two variables. ");
        m.Append("This is appropriate for ordinal or non-normally distributed continuous data. ");
        m.Append("The calculation was performed locally by deterministic code.");

        var res = new StringBuilder();
        res.Append($"A total of {r.PairN} complete pair(s) were analyzed{MissingSuffix(r.DroppedForMissing, r.DroppedInvalid)}. ");
        res.Append($"Spearman's rho was {r.RhoDisplay}. ");
        if (r.TStatistic is not null)
            res.Append($"The test statistic was t = {InferenceMath.FormatNumber(r.TStatistic, 3)} with {r.DegreesOfFreedom} degrees of freedom, and the two-sided p-value was {r.PValueDisplay}. ");
        else
            res.Append($"The two-sided p-value was {r.PValueDisplay}. ");
        res.Append(SignificanceSentence(r.PValue, "The monotonic association", "a monotonic association"));
        if (r.StrengthBand.Length > 0) res.Append($"This corresponds to a {r.StrengthBand}. ");
        if (r.IsRobustAlternative)
            res.Append("Spearman's rank correlation was used here as a rank-based alternative to Pearson's correlation. ");
        if (r.TiesPresent && r.TieNote.Length > 0) res.Append(r.TieNote.Trim() + " ");
        res.Append("A correlation quantifies association only and does not imply causation.");

        var n = new StringBuilder();
        n.Append(DeterministicFooter + " ");
        n.Append("Spearman's rho reflects a monotonic association and need not be linear. ");
        n.Append("This is a crude (unadjusted) association and was not adjusted for confounders. ");
        n.Append("Correlation does not imply causation. ");
        AppendNonSignificantCaution(n, r.PValue);
        if (r.StrengthBand.Length > 0) n.Append(HeuristicBandNote);
        if (isStale) n.Append(StaleWarning + " ");
        n.Append(NoRowsFooter);

        return Compose($"Methods and Results — {x} vs {y}", r.ResultTitle, m, res, n);
    }

    // =====================================================================
    // Mann-Whitney U (rank test, two groups) — COMPUTED.
    // =====================================================================
    private static ResearchLabNarrativeResult BuildMannWhitneyComputed(RankTestResult r, bool isStale)
    {
        string ranked = Safe(r.RankedDisplay, "the outcome");
        string grouping = Safe(r.GroupingDisplay, "the grouping variable");

        var m = new StringBuilder();
        m.Append($"The distribution of {ranked} was compared between the two independent groups defined by {grouping} ");
        m.Append("using the Mann-Whitney U test, a rank-based comparison that does not assume normally distributed data. ");
        m.Append("This is appropriate for ordinal or non-normal continuous outcomes. ");
        if (r.TieCorrectionApplied) m.Append("A tie correction was applied. ");
        m.Append("The calculation was performed locally by deterministic code.");

        var res = new StringBuilder();
        res.Append($"A total of {r.ValidN} participant(s) were analyzed{MissingSuffix(r.DroppedForMissing, r.DroppedInvalid)}. ");
        if (r.Groups.Count > 0)
            res.Append($"The group rank summaries were {RankGroupsText(r.Groups)}. ");
        res.Append($"The Mann-Whitney U statistic was {InferenceMath.FormatNumber(r.U, 3)}");
        if (r.Z is not null) res.Append($", the normal-approximation z was {InferenceMath.FormatNumber(r.Z, 3)}");
        res.Append($", and the two-sided p-value was {r.PValueDisplay}. ");
        res.Append(SignificanceSentence(r.PValue, $"The difference in ranked {ranked} between the two groups", "a difference between the groups"));
        if (r.EffectValue is not null && r.EffectName.Length > 0)
            res.Append($"The effect size ({LowerFirstWord(r.EffectName)}) was {InferenceMath.FormatNumber(r.EffectValue, 3)}. ");
        if (r.TieCorrectionApplied && r.TieNote.Length > 0) res.Append(r.TieNote.Trim() + " ");
        res.Append("These results describe an observed rank-based group difference and do not imply causation.");

        var n = new StringBuilder();
        n.Append(DeterministicFooter + " ");
        n.Append("The Mann-Whitney U test is a rank-based comparison and does not assume normally distributed data. ");
        n.Append("This is a crude (unadjusted) comparison and was not adjusted for confounders. ");
        n.Append("An observed group difference does not imply causation. ");
        AppendNonSignificantCaution(n, r.PValue);
        if (isStale) n.Append(StaleWarning + " ");
        n.Append(NoRowsFooter);

        return Compose($"Methods and Results — {ranked} by {grouping}", r.ResultTitle, m, res, n);
    }

    // =====================================================================
    // Kruskal-Wallis (rank test, 3+ groups) — COMPUTED.
    // =====================================================================
    private static ResearchLabNarrativeResult BuildKruskalWallisComputed(RankTestResult r, bool isStale)
    {
        string ranked = Safe(r.RankedDisplay, "the outcome");
        string grouping = Safe(r.GroupingDisplay, "the grouping variable");
        int groups = r.Groups.Count;

        var m = new StringBuilder();
        m.Append($"The distribution of {ranked} was compared across the {groups} independent groups defined by {grouping} ");
        m.Append("using the Kruskal-Wallis test, a rank-based extension of one-way comparison that does not assume normally distributed data. ");
        m.Append("This is appropriate for ordinal or non-normal continuous outcomes. ");
        if (r.TieCorrectionApplied) m.Append("A tie correction was applied. ");
        m.Append("The calculation was performed locally by deterministic code.");

        var res = new StringBuilder();
        res.Append($"A total of {r.ValidN} participant(s) across {groups} group(s) were analyzed{MissingSuffix(r.DroppedForMissing, r.DroppedInvalid)}. ");
        if (r.Groups.Count > 0)
            res.Append($"The group rank summaries were {RankGroupsText(r.Groups)}. ");
        res.Append($"The Kruskal-Wallis H statistic was {InferenceMath.FormatNumber(r.H, 3)} with {r.DegreesOfFreedom} degrees of freedom, ");
        res.Append($"and the p-value was {r.PValueDisplay}. ");
        res.Append(SignificanceSentence(r.PValue, "The difference in ranks among the groups", "a difference among the groups"));
        if (r.EffectValue is not null && r.EffectName.Length > 0)
            res.Append($"The effect size ({LowerFirstWord(r.EffectName)}) was {InferenceMath.FormatNumber(r.EffectValue, 3)}. ");
        if (r.TieCorrectionApplied && r.TieNote.Length > 0) res.Append(r.TieNote.Trim() + " ");
        res.Append("A significant Kruskal-Wallis test indicates that at least one group differs; it does not identify which. ");
        res.Append("These results describe observed rank-based group differences and do not imply causation.");

        var n = new StringBuilder();
        n.Append(DeterministicFooter + " ");
        n.Append("Post-hoc pairwise comparisons were not performed, so this analysis does not identify which specific groups differ. ");
        n.Append("The Kruskal-Wallis test is a rank-based comparison and does not assume normally distributed data. ");
        n.Append("This is a crude (unadjusted) comparison and was not adjusted for confounders. ");
        n.Append("Observed differences among groups do not imply causation. ");
        if (isStale) n.Append(StaleWarning + " ");
        n.Append(NoRowsFooter);

        return Compose($"Methods and Results — {ranked} by {grouping}", r.ResultTitle, m, res, n);
    }

    // =====================================================================
    // Categorical association — chi-square / Fisher exact — COMPUTED.
    // =====================================================================
    private static ResearchLabNarrativeResult BuildCategoricalComputed(CategoricalTestResult r, bool isStale)
    {
        string outcome = Safe(r.OutcomeDisplay, "the row variable");
        string predictor = Safe(r.PredictorDisplay, "the column variable");
        int rows = r.Table?.RowLabels.Count ?? 0;
        int cols = r.Table?.ColumnLabels.Count ?? 0;
        bool hasDims = rows > 0 && cols > 0;
        string dims = hasDims ? $"{rows}×{cols}" : "";
        bool fisher = (r.TestUsed ?? "").IndexOf("Fisher", StringComparison.OrdinalIgnoreCase) >= 0
                      || (r.FisherPValue is not null && r.ChiSquare is null);
        bool chi = !fisher && (r.ChiSquare is not null
                      || (r.TestUsed ?? "").IndexOf("Chi-square", StringComparison.OrdinalIgnoreCase) >= 0);
        bool expectedLimited = !r.ExpectedCountsOk || r.CellsBelow5 > 0 || r.CellsBelow1 > 0;

        var m = new StringBuilder();
        m.Append($"The association between {outcome} (rows) and {predictor} (columns) was tested in a {(hasDims ? dims + " " : "")}contingency table. ");
        if (chi)
        {
            m.Append("A chi-square test of independence was used");
            m.Append(expectedLimited ? ", although some expected cell counts were small, which can affect the chi-square approximation. " : ". ");
        }
        if (fisher)
            m.Append("Fisher's exact test was used, which is appropriate for small expected cell counts. ");
        m.Append("The calculation was performed locally by deterministic code.");

        var res = new StringBuilder();
        res.Append($"A total of {r.ValidPairs} complete case(s) were analyzed{MissingSuffix(r.DroppedForMissing, 0)}");
        if (hasDims) res.Append($" in a {dims} table");
        res.Append(". ");
        if (chi)
            res.Append($"The chi-square statistic was {InferenceMath.FormatNumber(r.ChiSquare, 3)} with {r.DegreesOfFreedom} degrees of freedom, and the p-value was {r.PValueDisplay}. ");
        else if (fisher)
            res.Append($"Fisher's exact two-sided p-value was {r.PValueDisplay}. ");
        else
            res.Append($"The association p-value was {r.PValueDisplay}. ");
        res.Append(SignificanceSentence(r.PValue, $"The association between {outcome} and {predictor}", "an association"));
        if (r.CramersV is not null || r.Phi is not null)
        {
            var parts = new List<string>();
            if (r.CramersV is not null) parts.Add($"Cramér's V = {InferenceMath.FormatNumber(r.CramersV, 3)}");
            if (r.Phi is not null) parts.Add($"phi = {InferenceMath.FormatNumber(r.Phi, 3)}");
            res.Append($"The effect size was {string.Join(", ", parts)}. ");
        }
        res.Append("These results describe a statistical association and do not imply causation.");

        var n = new StringBuilder();
        n.Append(DeterministicFooter + " ");
        n.Append("This test describes a statistical association between two categorical variables; association does not imply causation. ");
        n.Append("This is a crude (unadjusted) association and was not adjusted for confounders. ");
        if (expectedLimited)
            n.Append("Some expected cell counts were small, which can make the chi-square approximation less reliable; interpret the p-value with caution. ");
        if (rows == 2 && cols == 2)
            n.Append("For a binary-by-binary (2×2) table, the dedicated 2×2 measures template (odds ratio and design-appropriate risk/prevalence measures with confidence intervals) is the preferred effect summary and is handled separately. ");
        if (isStale) n.Append(StaleWarning + " ");
        n.Append(NoRowsFooter);

        return Compose($"Methods and Results — {outcome} × {predictor}", r.ResultTitle, m, res, n);
    }

    // =====================================================================
    // Generic fallback for any result type without a dedicated template.
    // Conservative — no numbers are parsed or restated; only the title and
    // computed-state are used.
    // =====================================================================
    private static ResearchLabNarrativeResult BuildGenericFallback(IInferenceExportable r, bool isStale)
    {
        string title = Safe(r.ResultTitle, "Computed result");
        string methods, results;
        if (r.Computed)
        {
            methods =
                "A statistical analysis was computed locally using the selected Research Lab method. "
                + "Estimates describe the analyzed data and were produced deterministically on this device.";
            results =
                "The computed result is available in the result details and export. "
                + "A tailored Methods and Results narrative template has not been implemented for this result type yet, "
                + "so specific numeric values are intentionally not restated here.";
        }
        else
        {
            methods =
                "A statistical analysis was requested using the selected Research Lab method, "
                + "but it was not computed, so no narrative values are available.";
            results = "No computed result is available. See the result details for the reason.";
        }

        var n = new StringBuilder();
        n.Append("A tailored narrative template is not available for this result type yet. ");
        n.Append(DeterministicFooter + " ");
        n.Append("Association does not imply causation. ");
        if (isStale) n.Append(StaleWarning + " ");
        n.Append(NoRowsFooter);

        return new ResearchLabNarrativeResult
        {
            Title = $"Methods and Results — {title}",
            SourceResultTitle = r.ResultTitle ?? "",
            MethodsText = methods,
            ResultsText = results,
            NotesText = n.ToString().Trim(),
        };
    }

    // =====================================================================
    // Null result — nothing to narrate. Never throws.
    // =====================================================================
    private static ResearchLabNarrativeResult BuildNullResult(bool isStale)
    {
        var n = new StringBuilder();
        n.Append("No computed result was provided, so there is nothing to narrate. ");
        n.Append(DeterministicFooter + " ");
        if (isStale) n.Append(StaleWarning + " ");
        n.Append(NoRowsFooter);

        return new ResearchLabNarrativeResult
        {
            Title = "Methods and Results — no result",
            SourceResultTitle = "",
            MethodsText = "No statistical result was supplied to the narrative generator.",
            ResultsText = "No results are available.",
            NotesText = n.ToString().Trim(),
        };
    }

    // Last-resort DTO if an unexpected exception is caught. Aggregate-safe.
    private static ResearchLabNarrativeResult BuildSafeError(IInferenceExportable? result, bool isStale)
    {
        var n = new StringBuilder();
        n.Append("A narrative could not be generated for this result. ");
        n.Append(DeterministicFooter + " ");
        if (isStale) n.Append(StaleWarning + " ");
        n.Append(NoRowsFooter);

        return new ResearchLabNarrativeResult
        {
            Title = "Methods and Results — unavailable",
            SourceResultTitle = result?.ResultTitle ?? "",
            MethodsText = "A tailored narrative template is not available for this result.",
            ResultsText = "No results are available.",
            NotesText = n.ToString().Trim(),
        };
    }

    // ---------------------------------------------------------------------
    // Small, pure formatting helpers (no statistics, no I/O).
    // ---------------------------------------------------------------------
    private static string Safe(string? s, string fallback)
    {
        string t = (s ?? "").Trim();
        return t.Length > 0 ? t : fallback;
    }

    // Lowercases only the first character (e.g. "Risk ratio" → "risk ratio",
    // "Prevalence ratio" → "prevalence ratio"); leaves the rest untouched.
    private static string LowerFirstWord(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return char.ToLowerInvariant(s[0]) + s.Substring(1);
    }

    // Human phrase for the design gate (only used when risk measures ARE
    // reported, i.e. cohort or cross-sectional).
    private static string DesignPhrase(TwoByTwoStudyDesignKind kind) => kind switch
    {
        TwoByTwoStudyDesignKind.Cohort => "cohort or RCT",
        TwoByTwoStudyDesignKind.CrossSectional => "cross-sectional",
        _ => "an eligible design",
    };

    // Heuristic-band reminder note (used only when a strength band is shown).
    private const string HeuristicBandNote =
        "Effect-size strength bands are heuristic, descriptive labels only and should not be over-interpreted. ";

    // Assembles a DTO from the three builders and trims each section.
    private static ResearchLabNarrativeResult Compose(string title, string sourceTitle, StringBuilder m, StringBuilder res, StringBuilder n)
        => new ResearchLabNarrativeResult
        {
            Title = title,
            SourceResultTitle = sourceTitle ?? "",
            MethodsText = m.ToString().Trim(),
            ResultsText = res.ToString().Trim(),
            NotesText = n.ToString().Trim(),
        };

    // Shared "not computed / not runnable" narrative for the non-2×2 result
    // types. Never restates or invents a number.
    private static ResearchLabNarrativeResult BuildBlocked(string testName, string sourceTitle, string subject, bool notRunnable, string statusReason, bool isStale)
    {
        string suffix = notRunnable ? "not runnable" : "not computed";
        string methods = notRunnable
            ? $"A {testName} was requested for {subject}, but a Methods and Results narrative is not available because this pairing is not runnable or not appropriate for the test."
            : $"A {testName} was requested for {subject}, but a Methods and Results narrative is not available because the result was not computed.";

        var res = new StringBuilder();
        res.Append("No statistical results were produced for this pairing. ");
        if (!string.IsNullOrWhiteSpace(statusReason)) res.Append(statusReason.Trim() + " ");
        res.Append("No test statistic, p-value, or effect size was calculated.");

        var n = new StringBuilder();
        n.Append("Resolve the reason above and re-run the analysis to obtain a statistical narrative. ");
        n.Append(DeterministicFooter + " ");
        if (isStale) n.Append(StaleWarning + " ");
        n.Append(NoRowsFooter);

        return new ResearchLabNarrativeResult
        {
            Title = $"Methods and Results — {Safe(sourceTitle, testName)} ({suffix})",
            SourceResultTitle = sourceTitle ?? "",
            MethodsText = methods,
            ResultsText = res.ToString().Trim(),
            NotesText = n.ToString().Trim(),
        };
    }

    // Significance sentence with strict wording: never "no association" /
    // "no difference". subjectForSig reads as a sentence subject; nounForNonSig
    // reads after "no statistically significant evidence of ...".
    private static string SignificanceSentence(double? p, string subjectForSig, string nounForNonSig)
    {
        if (p is not double pv) return "";
        return pv < 0.05
            ? $"{subjectForSig} was statistically significant at the conventional 0.05 significance level. "
            : $"There was no statistically significant evidence of {nounForNonSig} at the conventional 0.05 significance level "
              + "(a non-significant result does not, by itself, establish the absence of an effect). ";
    }

    // Adds a non-overclaim caution to a Notes builder only when p ≥ 0.05.
    private static void AppendNonSignificantCaution(StringBuilder n, double? p)
    {
        if (p is double pv && pv >= 0.05)
            n.Append("A non-significant result should not be interpreted as evidence that no relationship exists. ");
    }

    // "" or " (excluded for missing values: X)" / "; invalid: Y" — aggregate counts only.
    private static string MissingSuffix(int droppedMissing, int droppedInvalid)
    {
        var parts = new List<string>();
        if (droppedMissing > 0) parts.Add($"excluded for missing values: {droppedMissing}");
        if (droppedInvalid > 0) parts.Add($"excluded as invalid: {droppedInvalid}");
        return parts.Count == 0 ? "" : $" ({string.Join("; ", parts)})";
    }

    // Aggregate group summaries — label, n, mean, SD. No participant rows.
    private static string WelchGroupsText(List<WelchGroupSummary> gs) =>
        string.Join("; ", gs.Select(g =>
            $"{Safe(g.Label, "group")} (n = {g.N}, mean = {InferenceMath.FormatNumber(g.Mean, 3)}, SD = {InferenceMath.FormatNumber(g.Sd, 3)})"));

    private static string AnovaGroupsText(List<AnovaGroupSummary> gs) =>
        string.Join("; ", gs.Select(g =>
            $"{Safe(g.Label, "group")} (n = {g.N}, mean = {InferenceMath.FormatNumber(g.Mean, 3)}, SD = {InferenceMath.FormatNumber(g.Sd, 3)})"));

    private static string RankGroupsText(List<RankGroupSummary> gs) =>
        string.Join("; ", gs.Select(g =>
            $"{Safe(g.Label, "group")} (n = {g.N}, mean rank = {InferenceMath.FormatNumber(g.MeanRank, 2)})"));

    // Cleans an engine TestUsed string for a "requested a …" sentence: strips a
    // trailing "(not computed)" and falls back when it is empty/placeholder.
    private static string CleanName(string? testUsed, string fallback)
    {
        string t = (testUsed ?? "").Trim();
        int paren = t.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0) t = t.Substring(0, paren).Trim();
        if (t.Length == 0 || t.Equals("Not computed", StringComparison.OrdinalIgnoreCase)) return fallback;
        return t;
    }

    // Clean rank-test name for a blocked narrative (may be un-computed).
    private static string RankTestName(RankTestResult r)
    {
        string t = r.TestUsed ?? "";
        if (t.IndexOf("Mann-Whitney", StringComparison.OrdinalIgnoreCase) >= 0) return "Mann-Whitney U test";
        if (t.IndexOf("Kruskal", StringComparison.OrdinalIgnoreCase) >= 0) return "Kruskal-Wallis test";
        if (r.U is not null) return "Mann-Whitney U test";
        if (r.H is not null) return "Kruskal-Wallis test";
        return "rank-based test";
    }

    // Distinguishes the two rank tests carried by one result class: Kruskal-
    // Wallis populates H (3+ groups); Mann-Whitney populates U (two groups).
    private static bool IsKruskalWallis(RankTestResult r)
    {
        if (r.H is not null) return true;
        if (r.U is not null) return false;
        return (r.TestUsed ?? "").IndexOf("Kruskal", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
