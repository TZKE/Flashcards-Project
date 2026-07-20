using AIFlashcardMaker.ChartsStudio.Domain.Context;

namespace AIFlashcardMaker.ChartsStudio.Domain.ChartTypes;

/// <summary>
/// Charts Studio Phase 2 — the catalogue of chart forms Charts Studio can draw.
///
/// WHY THIS SET, AND ONLY THIS SET
///
/// Charts Studio renders exclusively from the aggregates Research Lab has already computed —
/// it never reads the dataset. Research Lab persists, per variable:
///
///   • continuous → Mean, SD, Median, Q1, Q3, Min, Max, ValidN, MissingN
///   • categorical → the observed categories and their counts
///
/// That is a complete five-number summary and a complete frequency table, which is exactly
/// enough for the three forms below and no more. In particular:
///
///   • A HISTOGRAM needs bin counts or the raw values. Neither is persisted.
///   • A SCATTER PLOT needs paired raw values. Not persisted.
///   • A GROUPED box plot needs a five-number summary PER GROUP. Research Lab computes
///     descriptive statistics per variable, not per variable per group.
///
/// Those three are therefore blocked on Research Lab persisting more aggregates — not on
/// charting work, and not on ScottPlot. Adding them here without the data would mean inventing
/// numbers, which is the one thing this module exists not to do.
///
/// ADDING A FORM LATER is adding a descriptor plus a renderer branch. Nothing in the
/// recommendation engine, contact sheet, render queue or persistence changes.
/// </summary>
public static class ChartTypeRegistry
{
    public const string BoxPlotId = "box-plot";
    public const string BarChartId = "bar-chart";
    public const string MeanSdId = "mean-sd";

    private static readonly ChartTypeDescriptor[] All =
    {
        new()
        {
            Id = BoxPlotId,
            DisplayName = "Box plot",
            Category = ChartCategory.Distribution,
            Description = "Median, interquartile range and full spread of a continuous measure.",
            BestFor = "Showing the shape of a continuous variable, including skew and spread.",
            AvoidWhen = "Fewer than about five observations — a dot plot is more honest.",
            BaseScore = 100,
            Applies = v =>
            {
                if (v.Kind != ContextVariableKind.Continuous)
                    return ChartApplicability.No("Box plots need a continuous variable.");
                if (!v.IsObservedDataAvailable)
                    return ChartApplicability.No("Run descriptive statistics in Research Lab first.");
                if (!v.HasFiveNumberSummary)
                    return ChartApplicability.No("This variable has no complete five-number summary.");
                return ChartApplicability.Yes;
            }
        },

        new()
        {
            Id = BarChartId,
            DisplayName = "Bar chart",
            Category = ChartCategory.Composition,
            Description = "Counts for each category of a grouping variable.",
            BestFor = "Comparing how many observations fall into each category.",
            AvoidWhen = "There are many categories — the bars become unreadable.",
            BaseScore = 90,
            Applies = v =>
            {
                bool categorical = v.Kind is ContextVariableKind.Binary
                                          or ContextVariableKind.Nominal
                                          or ContextVariableKind.Ordinal;
                if (!categorical)
                    return ChartApplicability.No("Bar charts need a categorical variable.");
                if (!v.IsObservedDataAvailable)
                    return ChartApplicability.No("Run descriptive statistics in Research Lab first.");
                if (!v.HasCategories)
                    return ChartApplicability.No("No categories were observed for this variable.");
                if (v.Categories.Count > 25)
                    return ChartApplicability.No($"{v.Categories.Count} categories is too many to read as bars.");
                return ChartApplicability.Yes;
            }
        },

        new()
        {
            Id = MeanSdId,
            DisplayName = "Mean ± SD",
            Category = ChartCategory.Distribution,
            Description = "Mean with a standard-deviation interval for a continuous measure.",
            BestFor = "A compact summary when the distribution is roughly symmetric.",
            AvoidWhen = "The data are skewed — a box plot shows what a mean would hide.",
            BaseScore = 60,
            Applies = v =>
            {
                if (v.Kind != ContextVariableKind.Continuous)
                    return ChartApplicability.No("This needs a continuous variable.");
                if (!v.IsObservedDataAvailable)
                    return ChartApplicability.No("Run descriptive statistics in Research Lab first.");
                if (!v.HasMeanAndSd)
                    return ChartApplicability.No("Mean and standard deviation are not both available.");
                return ChartApplicability.Yes;
            }
        }
    };

    public static IReadOnlyList<ChartTypeDescriptor> Descriptors => All;

    public static ChartTypeDescriptor? Find(string id) =>
        All.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal));

    /// <summary>Every form that can actually be drawn for this variable right now.</summary>
    public static IEnumerable<ChartTypeDescriptor> ApplicableTo(ContextVariable variable) =>
        All.Where(d => d.Applies(variable).IsApplicable);
}
