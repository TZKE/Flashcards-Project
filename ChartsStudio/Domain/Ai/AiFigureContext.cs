using AIFlashcardMaker.ChartsStudio.Domain.ChartTypes;
using AIFlashcardMaker.ChartsStudio.Domain.Context;
using AIFlashcardMaker.ChartsStudio.Domain.Themes;

namespace AIFlashcardMaker.ChartsStudio.Domain.Ai;

/// <summary>
/// Charts Studio Phase 6 — the bounded, DATA-FREE description of one figure that AI is allowed
/// to see.
///
/// THE PAYLOAD BOUNDARY, ENFORCED BY CONSTRUCTION. This type is what a prompt is built from,
/// and it has NO field capable of holding a data row — only the figure's title, its chart form,
/// its variable's name and units, its N, and the SUMMARY STATISTICS Research Lab already
/// computed, carried as strings. A future contributor cannot leak raw data into a prompt,
/// because there is nowhere in this type to put it. Statistics are copied verbatim from the
/// context, never recomputed, so a caption can never state a number the analysis did not
/// produce.
/// </summary>
public sealed class AiFigureContext
{
    public required int Index { get; init; }           // 1-based shelf position
    public required string Title { get; init; }
    public required string ChartTypeName { get; init; }
    public required string VariableName { get; init; }
    public string Units { get; init; } = "";

    /// <summary>Valid observations, e.g. "84". Empty when unknown.</summary>
    public string ValidN { get; init; } = "";
    public string MissingN { get; init; } = "";

    /// <summary>Pre-formatted summary lines, e.g. "median 24.5", "IQR 22.1–27.3". These are
    /// the ONLY numbers the model sees, and it is instructed to use only these.</summary>
    public IReadOnlyList<string> SummaryFacts { get; init; } = Array.Empty<string>();

    /// <summary>Observed category labels + counts for a categorical figure, e.g. "Female: 46".</summary>
    public IReadOnlyList<string> CategoryFacts { get; init; } = Array.Empty<string>();

    // ---- Style facts (names, not library types) -------------------------------------

    public string SeriesColorName { get; init; } = "";
    public string FontFamily { get; init; } = "";
    public bool ShowGrid { get; init; }
    public bool ShowLegend { get; init; }
    public bool ColorByCategory { get; init; }
    public string PaletteName { get; init; } = "";
    public bool UserEdited { get; init; }

    /// <summary>The user's current caption, if any — so a caption redraft can improve on it.</summary>
    public string ExistingCaption { get; init; } = "";

    /// <summary>
    /// Builds the context from the resolved style and the figure's variable. This is the single
    /// projection point; if a field could carry data, it would be caught here in review.
    /// </summary>
    public static AiFigureContext Build(
        int index,
        Specs.FigureSpec spec,
        ResolvedFigureStyle style,
        ContextVariable? variable,
        bool userEdited)
    {
        var summaryFacts = new List<string>();
        var categoryFacts = new List<string>();

        if (variable is not null)
        {
            if (variable.HasFiveNumberSummary)
            {
                summaryFacts.Add($"median {Fmt(variable.Median)}");
                summaryFacts.Add($"IQR {Fmt(variable.Q1)}–{Fmt(variable.Q3)}");
                summaryFacts.Add($"range {Fmt(variable.Min)}–{Fmt(variable.Max)}");
            }
            if (variable.HasMeanAndSd)
            {
                summaryFacts.Add($"mean {Fmt(variable.Mean)}");
                summaryFacts.Add($"SD {Fmt(variable.StdDev)}");
            }
            foreach (var c in variable.Categories)
                categoryFacts.Add($"{c.DisplayLabel}: {c.Count}");
        }

        var chartType = ChartTypeRegistry.Find(spec.ChartTypeId);

        return new AiFigureContext
        {
            Index = index,
            Title = style.Title,
            ChartTypeName = chartType?.DisplayName ?? spec.ChartTypeId,
            VariableName = variable?.DisplayName ?? "",
            Units = variable?.Units ?? "",
            ValidN = variable?.ValidN?.ToString() ?? "",
            MissingN = variable?.MissingN?.ToString() ?? "",
            SummaryFacts = summaryFacts,
            CategoryFacts = categoryFacts,
            SeriesColorName = style.SeriesFillHex,
            FontFamily = style.FontFamily,
            ShowGrid = style.ShowGrid,
            ShowLegend = style.ShowLegend,
            ColorByCategory = style.CategoryPalette is { Length: > 0 },
            PaletteName = style.CategoryPalette is { Length: > 0 } ? "category palette" : "single colour",
            UserEdited = userEdited,
            ExistingCaption = style.Caption
        };
    }

    private static string Fmt(double? v) =>
        v?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "—";
}

/// <summary>Data-free description of the whole figure set, for set-level review.</summary>
public sealed class AiFigureSetContext
{
    public required IReadOnlyList<AiFigureContext> Figures { get; init; }
    public required string StudyTitle { get; init; }
    public int Count => Figures.Count;
}
