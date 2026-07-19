using AIFlashcardMaker.ChartsStudio.Application.Rendering;
using AIFlashcardMaker.ChartsStudio.Domain.ChartTypes;
using AIFlashcardMaker.ChartsStudio.Domain.Context;

namespace AIFlashcardMaker.ChartsStudio.Infrastructure.Rendering;

/// <summary>
/// Charts Studio Phase 2 — the ScottPlot 5 renderer.
///
/// THE ONLY FILE IN THE MODULE THAT REFERENCES A CHARTING LIBRARY. Everything above works in
/// specs and contexts, so swapping engines means writing one new implementation of
/// IFigureRenderer and changing nothing else.
///
/// ScottPlot 5 was chosen by spike over OxyPlot and LiveCharts2. The deciding measurements:
/// ScottPlot and OxyPlot both held WYSIWYG exactly from 96 to 600 DPI — identical layout AND
/// identical tick labels — while LiveCharts2 produced a different figure at print size (tick
/// density exploded, type microscopic). Between the two survivors, ScottPlot has materially
/// more active development, a first-party WPF control for the interactive canvas, and a better
/// model for the custom plottables Kaplan-Meier, ROC and forest plots will need.
///
/// WYSIWYG IS THE CONTRACT. Every render — thumbnail, canvas, export — goes through this one
/// method, and print-resolution output uses ScottPlot's ScaleFactor so a large raster is the
/// same figure enlarged rather than the same figure with unreadable type.
///
/// RENDERS FROM AGGREGATES ONLY. Every number drawn here was computed by Research Lab and
/// projected into the context. This renderer never sees a dataset and never calculates a
/// statistic — it cannot, because the data it would need is not reachable from here.
/// </summary>
public sealed class ScottPlotFigureRenderer : IFigureRenderer
{
    public RenderResult Render(RenderRequest request, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var variable = request.Context.Variables.FirstOrDefault(v =>
                string.Equals(v.Id, request.Spec.PrimaryVariableId, StringComparison.Ordinal));

            if (variable is null)
                return RenderResult.Failure("The variable this figure was built from is no longer in the project.");

            var plot = new ScottPlot.Plot();
            ApplyTheme(plot);

            switch (request.Spec.ChartTypeId)
            {
                case ChartTypeRegistry.BoxPlotId:
                    if (!DrawBoxPlot(plot, variable)) return RenderResult.Failure("No five-number summary available.");
                    break;

                case ChartTypeRegistry.BarChartId:
                    if (!DrawBarChart(plot, variable)) return RenderResult.Failure("No categories available.");
                    break;

                case ChartTypeRegistry.MeanSdId:
                    if (!DrawMeanSd(plot, variable)) return RenderResult.Failure("Mean and SD are not both available.");
                    break;

                default:
                    return RenderResult.Failure($"Unknown chart type '{request.Spec.ChartTypeId}'.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(request.Spec.Title)) plot.Title(request.Spec.Title);
            if (!string.IsNullOrWhiteSpace(request.Spec.ValueAxisLabel)) plot.YLabel(request.Spec.ValueAxisLabel);
            if (!string.IsNullOrWhiteSpace(request.Spec.CategoryAxisLabel)) plot.XLabel(request.Spec.CategoryAxisLabel);

            // The WYSIWYG lever: type and line weights scale with the raster.
            plot.ScaleFactor = (float)request.ScaleFactor;

            cancellationToken.ThrowIfCancellationRequested();

            byte[] png = plot.GetImageBytes(
                request.WidthPixels,
                request.HeightPixels,
                ScottPlot.ImageFormat.Png);

            return png.Length > 0
                ? RenderResult.Success(png)
                : RenderResult.Failure("The figure produced no image.");
        }
        catch (OperationCanceledException)
        {
            throw;   // cancellation is the queue's business, not a figure failure
        }
        catch (Exception ex)
        {
            // A renderer fault degrades to a card that explains itself. It never takes down the
            // contact sheet, and it never shows a blank frame pretending to be a figure.
            return RenderResult.Failure($"This figure could not be drawn ({ex.GetType().Name}).");
        }
    }

    // ---------------------------------------------------------------------------------
    // Theme — one sober publication default. The full theme system is a later phase.
    // ---------------------------------------------------------------------------------

    private static void ApplyTheme(ScottPlot.Plot plot)
    {
        plot.FigureBackground.Color = ScottPlot.Colors.White;
        plot.DataBackground.Color = ScottPlot.Colors.White;
        plot.Axes.Color(ScottPlot.Color.FromHex("#333333"));
        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#ECECEC");
    }

    private static readonly ScottPlot.Color SeriesFill = ScottPlot.Color.FromHex("#2F6FB2");
    private static readonly ScottPlot.Color SeriesLine = ScottPlot.Color.FromHex("#1B4A7A");

    // ---------------------------------------------------------------------------------
    // Forms
    // ---------------------------------------------------------------------------------

    private static bool DrawBoxPlot(ScottPlot.Plot plot, ContextVariable v)
    {
        if (!v.HasFiveNumberSummary) return false;

        var box = new ScottPlot.Box
        {
            Position = 1,
            BoxMin = v.Q1!.Value,
            BoxMiddle = v.Median!.Value,
            BoxMax = v.Q3!.Value,
            WhiskerMin = v.Min!.Value,
            WhiskerMax = v.Max!.Value,
            Width = 0.6,
            FillColor = SeriesFill.WithAlpha(0.75),
            LineColor = SeriesLine
        };

        plot.Add.Boxes(new[] { box });

        plot.Axes.SetLimitsX(0.2, 1.8);
        plot.HideGrid();
        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual();
        return true;
    }

    private static bool DrawBarChart(ScottPlot.Plot plot, ContextVariable v)
    {
        if (!v.HasCategories) return false;

        var bars = new List<ScottPlot.Bar>();
        var ticks = new List<ScottPlot.Tick>();

        for (int i = 0; i < v.Categories.Count; i++)
        {
            var c = v.Categories[i];
            bars.Add(new ScottPlot.Bar
            {
                Position = i,
                Value = c.Count,
                FillColor = SeriesFill,
                LineColor = SeriesLine,
                Size = 0.7
            });
            ticks.Add(new ScottPlot.Tick(i, c.DisplayLabel));
        }

        plot.Add.Bars(bars);

        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(ticks.ToArray());
        plot.Axes.Bottom.MajorTickStyle.Length = 0;
        plot.Axes.SetLimitsY(0, bars.Max(b => b.Value) * 1.15);
        return true;
    }

    private static bool DrawMeanSd(ScottPlot.Plot plot, ContextVariable v)
    {
        if (!v.HasMeanAndSd) return false;

        double mean = v.Mean!.Value, sd = v.StdDev!.Value;

        var marker = plot.Add.Marker(1, mean);
        marker.MarkerStyle.Shape = ScottPlot.MarkerShape.FilledCircle;
        marker.MarkerStyle.Size = 12;
        marker.MarkerStyle.FillColor = SeriesFill;

        var errorBar = plot.Add.ErrorBar(
            xs: new[] { 1.0 },
            ys: new[] { mean },
            yErrors: new[] { sd });
        errorBar.Color = SeriesLine;

        plot.Axes.SetLimitsX(0.2, 1.8);
        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual();
        return true;
    }
}
