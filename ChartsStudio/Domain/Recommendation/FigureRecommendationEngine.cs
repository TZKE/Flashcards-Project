using AIFlashcardMaker.ChartsStudio.Domain.ChartTypes;
using AIFlashcardMaker.ChartsStudio.Domain.Context;
using AIFlashcardMaker.ChartsStudio.Domain.Specs;

namespace AIFlashcardMaker.ChartsStudio.Domain.Recommendation;

/// <summary>
/// Charts Studio Phase 2 — the deterministic recommender.
///
/// Enumerates every figure that can honestly be drawn from the aggregates Research Lab already
/// computed, scores them, and writes the reason. Pure: no I/O, no network, no AI, no clock.
/// The same context always produces the same ordering, which is what makes golden tests
/// possible — and it is why this, not a language model, decides what the contact sheet shows.
///
/// The AI advisory layer (a later phase) sits ON TOP: it may reorder these candidates and
/// reword the rationale, and it may never introduce a form the engine did not validate.
/// </summary>
public sealed class FigureRecommendationEngine
{
    /// <summary>How many proposals the contact sheet opens with.</summary>
    /// <remarks>
    /// Six is reviewable at a glance. Twelve is a chore, and choice fatigue undoes the point of
    /// proposing anything at all.
    /// </remarks>
    public const int DefaultLimit = 6;

    /// <summary>
    /// Produces the ranked proposals for a project.
    /// </summary>
    public IReadOnlyList<FigureCandidate> Recommend(AnalysisContext context, int limit = DefaultLimit)
    {
        if (context is null || !context.IsLoaded) return Array.Empty<FigureCandidate>();

        var candidates = new List<FigureCandidate>();

        foreach (var variable in context.Variables)
        {
            if (!variable.IsChartable) continue;

            foreach (var chartType in ChartTypeRegistry.Descriptors)
            {
                if (!chartType.Applies(variable).IsApplicable) continue;
                candidates.Add(Build(chartType, variable, context));
            }
        }

        return candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Variable.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.ChartType.Id, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Every applicable figure for one variable, unranked and unlimited. Backs the
    /// "Add a figure" surface, where the user has already chosen what they are looking at.
    /// </summary>
    public IReadOnlyList<FigureCandidate> RecommendForVariable(AnalysisContext context, string variableId)
    {
        if (context is null || !context.IsLoaded) return Array.Empty<FigureCandidate>();

        var variable = context.Variables.FirstOrDefault(v =>
            string.Equals(v.Id, variableId, StringComparison.Ordinal));

        if (variable is null || !variable.IsChartable) return Array.Empty<FigureCandidate>();

        return ChartTypeRegistry.Descriptors
            .Where(t => t.Applies(variable).IsApplicable)
            .Select(t => Build(t, variable, context))
            .OrderByDescending(c => c.Score)
            .ToList();
    }

    // ---------------------------------------------------------------------------------

    private static FigureCandidate Build(
        ChartTypeDescriptor chartType,
        ContextVariable variable,
        AnalysisContext context)
    {
        var spec = new FigureSpec
        {
            ChartTypeId = chartType.Id,
            VariableIds = { variable.Id },
            Title = ComposeTitle(chartType, variable),
            ValueAxisLabel = ComposeValueAxisLabel(chartType, variable),
            CategoryAxisLabel = ComposeCategoryAxisLabel(chartType, variable),
            SourceFingerprint = context.Fingerprint.Value
        };

        return new FigureCandidate
        {
            Spec = spec,
            ChartType = chartType,
            Variable = variable,
            Score = Score(chartType, variable),
            Rationale = ComposeRationale(chartType, variable),
            Cautions = ComposeCautions(chartType, variable)
        };
    }

    // ---- Scoring --------------------------------------------------------------------
    //
    // Ordering rules, in the order they matter:
    //   1. The form's own suitability (BaseScore) — a box plot says more than a mean ± SD.
    //   2. Outcome variables first: a paper's figures are about its outcomes.
    //   3. Predictors next, then unclear roles.
    //   4. Penalise figures the reader should not trust much: tiny n, heavy missingness,
    //      many categories. A figure that will draw a reviewer's objection should not lead.

    private static int Score(ChartTypeDescriptor chartType, ContextVariable variable)
    {
        int score = chartType.BaseScore;

        score += variable.Role switch
        {
            ContextVariableRole.Outcome => 50,
            ContextVariableRole.Predictor => 25,
            _ => 0
        };

        int n = variable.ValidN ?? 0;
        if (n > 0 && n < 5) score -= 60;
        else if (n < 10) score -= 25;

        if (variable.ValidN is > 0 && variable.MissingN is > 0)
        {
            double total = variable.ValidN.Value + variable.MissingN.Value;
            double missingPercent = variable.MissingN.Value / total * 100.0;
            if (missingPercent >= 40) score -= 40;
            else if (missingPercent >= 20) score -= 15;
        }

        if (variable.HasCategories && variable.Categories.Count > 10) score -= 20;

        return score;
    }

    // ---- Text -----------------------------------------------------------------------
    //
    // The card leads with what the figure is ABOUT, not what it is CALLED. A user who does not
    // know what a box plot is does know they want to see the spread of HbA1c.

    private static string ComposeTitle(ChartTypeDescriptor chartType, ContextVariable variable)
    {
        string name = variable.DisplayName;

        // Titles are what a caption would say, not what the chart is called. "sex by category"
        // reads as machine output; "Distribution of sex" reads as a figure title.
        return chartType.Id switch
        {
            ChartTypeRegistry.BoxPlotId => $"Distribution of {name}",
            ChartTypeRegistry.BarChartId => $"Distribution of {name}",
            ChartTypeRegistry.MeanSdId => $"Mean {name} (± SD)",
            _ => name
        };
    }

    private static string ComposeValueAxisLabel(ChartTypeDescriptor chartType, ContextVariable variable)
    {
        string units = string.IsNullOrWhiteSpace(variable.Units) ? "" : $" ({variable.Units})";

        return chartType.Id switch
        {
            ChartTypeRegistry.BarChartId => "Number of participants",
            _ => variable.DisplayName + units
        };
    }

    private static string ComposeCategoryAxisLabel(ChartTypeDescriptor chartType, ContextVariable variable) =>
        chartType.Id switch
        {
            ChartTypeRegistry.BarChartId => variable.DisplayName,
            _ => ""
        };

    private static string ComposeRationale(ChartTypeDescriptor chartType, ContextVariable variable)
    {
        string name = variable.DisplayName;

        return chartType.Id switch
        {
            ChartTypeRegistry.BoxPlotId =>
                $"{name} is continuous, so a box plot shows its median, spread and any skew — " +
                "detail a single summary number would hide.",

            ChartTypeRegistry.BarChartId =>
                $"{name} has {variable.Categories.Count} categories, so a bar chart compares how " +
                "many participants fall into each one.",

            ChartTypeRegistry.MeanSdId =>
                $"{name} is continuous, so a mean with a standard-deviation interval gives a " +
                "compact summary — best when the distribution is roughly symmetric.",

            _ => $"{chartType.DisplayName} suits {name}."
        };
    }

    private static IReadOnlyList<string> ComposeCautions(
        ChartTypeDescriptor chartType,
        ContextVariable variable)
    {
        var cautions = new List<string>();

        int n = variable.ValidN ?? 0;
        if (n > 0 && n < 5)
            cautions.Add($"Only {n} observations — too few to summarise reliably.");
        else if (n > 0 && n < 10)
            cautions.Add($"Only {n} observations — interpret with care.");

        if (variable.ValidN is > 0 && variable.MissingN is > 0)
        {
            double total = variable.ValidN.Value + variable.MissingN.Value;
            double missingPercent = variable.MissingN.Value / total * 100.0;
            if (missingPercent >= 20)
                cautions.Add($"{missingPercent:F0}% of values are missing.");
        }

        if (chartType.Id == ChartTypeRegistry.BarChartId && variable.Categories.Count > 10)
            cautions.Add($"{variable.Categories.Count} categories may be hard to read.");

        if (chartType.Id == ChartTypeRegistry.MeanSdId && variable.HasFiveNumberSummary)
        {
            // Mean sitting well away from the median is the cheapest available skew signal,
            // and skew is exactly when a mean ± SD misleads.
            double? mean = variable.Mean, median = variable.Median;
            double? q1 = variable.Q1, q3 = variable.Q3;
            if (mean.HasValue && median.HasValue && q1.HasValue && q3.HasValue)
            {
                double iqr = q3.Value - q1.Value;
                if (iqr > 0 && Math.Abs(mean.Value - median.Value) > iqr * 0.5)
                    cautions.Add("Mean and median differ noticeably — a box plot may represent this better.");
            }
        }

        return cautions;
    }
}
