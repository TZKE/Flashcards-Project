using System.Text;

namespace AIFlashcardMaker;

// ===========================================================================
// Research Lab — Phase 4F (Slice 1): deterministic Methods/Results narrative
// generator (BACKEND / HEADLESS ONLY).
//
// Turns an already-computed, in-memory inference result (an IInferenceExportable)
// into copy-ready academic "Methods" and "Results" prose. This slice ships a
// RICH template for the 2×2 measures result (odds ratio + design-gated risk/
// prevalence ratio & difference with 95% CIs) and a conservative GENERIC
// fallback for every other result type.
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
    // Generic fallback for any non-2×2 result type. Conservative — no numbers
    // are parsed or restated; only the title and computed-state are used.
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
}
