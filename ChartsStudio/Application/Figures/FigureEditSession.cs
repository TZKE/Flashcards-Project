using System.Globalization;
using AIFlashcardMaker.ChartsStudio.Domain.ChartTypes;
using AIFlashcardMaker.ChartsStudio.Domain.Specs;
using AIFlashcardMaker.ChartsStudio.Domain.Themes;
using AIFlashcardMaker.ChartsStudio.Domain.Validation;

namespace AIFlashcardMaker.ChartsStudio.Application.Figures;

/// <summary>Every editable field, for targeted reset and override indicators.</summary>
public enum EditField
{
    Title, Subtitle, Caption, XAxisTitle, YAxisTitle,
    Theme, Palette, ColorByCategory, SeriesColor, BackgroundColor,
    FontFamily, TitleFontSize, AxisFontSize, TickFontSize,
    LineWidth, MarkerSize, Opacity,
    ShowGrid, ShowLegend, ShowXAxis, ShowYAxis, Orientation
}

/// <summary>Sections group fields for section-level reset.</summary>
public enum EditSection { Text, Appearance, Layout }

/// <summary>
/// Charts Studio Phase 3 — the editor's brain, with no UI in it.
///
/// Owns the patch being edited, its history, validation and reset. The WPF view model is a
/// thin binding shim over this class; everything that can go wrong lives HERE, where the
/// headless QA harness can reach it without a dispatcher.
///
/// THE SPEC IS NEVER TOUCHED. This class holds the kept figure's spec read-only for defaults
/// and hands out resolved styles; every mutation goes through a freshly built patch committed
/// to history. That is the non-destructive guarantee, enforced by construction.
/// </summary>
public sealed class FigureEditSession
{
    private readonly KeptFigure _figure;
    private readonly PatchHistory _history;

    public FigureEditSession(KeptFigure figure)
    {
        _figure = figure ?? throw new ArgumentNullException(nameof(figure));
        _history = new PatchHistory(figure.Patch);
        ChartType = ChartTypeRegistry.Find(figure.Spec.ChartTypeId);
    }

    /// <summary>Raised after any state change — commit, undo, redo, reset. The view model
    /// re-reads everything and re-renders the preview on this signal.</summary>
    public event EventHandler? Changed;

    public FigureSpec Spec => _figure.Spec;
    public string FigureId => _figure.Id;
    public ChartTypeDescriptor? ChartType { get; }

    public FigurePatch? CurrentPatch => _history.Current;

    public ResolvedFigureStyle CurrentStyle => FigureStyleResolver.Resolve(_figure.Spec, _history.Current);

    /// <summary>Orientation is only offered where the form honours it.</summary>
    public bool SupportsOrientation => _figure.Spec.ChartTypeId == ChartTypeRegistry.BarChartId;

    public bool SupportsCategoryColors => _figure.Spec.ChartTypeId == ChartTypeRegistry.BarChartId;

    // ---- History --------------------------------------------------------------------

    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;
    public bool IsDirty => _history.IsDirty;

    public void Undo() { if (_history.Undo()) Raise(); }
    public void Redo() { if (_history.Redo()) Raise(); }

    /// <summary>Returns the canonical patch for persistence and marks this state saved.</summary>
    public FigurePatch? MarkSaved()
    {
        _history.MarkSaved();
        Raise();
        return FigurePatch.Canonicalize(_history.Current)?.Clone();
    }

    // ---- Validation -----------------------------------------------------------------

    /// <summary>Messages from the most recent attempted change. Empty when it was accepted.</summary>
    public IReadOnlyList<PatchValidationError> LastErrors { get; private set; } =
        Array.Empty<PatchValidationError>();

    // ---- Field setters --------------------------------------------------------------
    //
    // Every setter: clone current → mutate the clone → validate → commit or report.
    // Text setters normalise "same as the default" back to null, so retyping the original
    // title genuinely clears the override instead of storing a redundant copy of it.

    public void SetTitle(string? value) => Apply(p => p.Title = OverrideOrNull(value, _figure.Spec.Title));
    public void SetSubtitle(string? value) => Apply(p => p.Subtitle = OverrideOrNull(value, ""));
    public void SetCaption(string? value) => Apply(p => p.Caption = OverrideOrNull(value, ""));
    public void SetXAxisTitle(string? value) => Apply(p => p.XAxisTitle = OverrideOrNull(value, _figure.Spec.CategoryAxisLabel));
    public void SetYAxisTitle(string? value) => Apply(p => p.YAxisTitle = OverrideOrNull(value, _figure.Spec.ValueAxisLabel));

    public void SetTheme(string? themeId) => Apply(p =>
        p.ThemeId = string.IsNullOrWhiteSpace(themeId) || themeId == FigureThemes.DefaultThemeId ? null : themeId);

    public void SetPalette(string? paletteId) => Apply(p =>
        p.PaletteId = string.IsNullOrWhiteSpace(paletteId) || paletteId == FigureThemes.DefaultPaletteId ? null : paletteId);

    public void SetColorByCategory(bool value) => Apply(p => p.ColorByCategory = value ? true : null);

    public void SetSeriesColor(string? hex) => Apply(p => p.SeriesColorHex = NullIfBlank(hex));
    public void SetBackgroundColor(string? hex) => Apply(p => p.BackgroundColorHex = NullIfBlank(hex));
    public void SetFontFamily(string? family) => Apply(p => p.FontFamily = NullIfBlank(family));

    public void SetShowGrid(bool? value) => Apply(p => p.ShowGrid = value);
    public void SetShowLegend(bool? value) => Apply(p => p.ShowLegend = value);
    public void SetShowXAxis(bool? value) => Apply(p => p.ShowXAxis = value);
    public void SetShowYAxis(bool? value) => Apply(p => p.ShowYAxis = value);

    public void SetOrientation(string? value) => Apply(p =>
        p.Orientation = string.Equals(value, "horizontal", StringComparison.OrdinalIgnoreCase) ? "horizontal" : null);

    /// <summary>
    /// Numeric fields arrive as raw text from the UI. Empty clears the override; anything
    /// unparseable or out of range is rejected WITH a message and committed nowhere — the
    /// figure never renders from a value the user didn't successfully enter.
    /// </summary>
    public bool TrySetNumber(EditField field, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            Apply(p => SetNumeric(p, field, null));
            return true;
        }

        if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            && !double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            LastErrors = new[] { new PatchValidationError { Field = FieldLabel(field), Message = "Not a number." } };
            Raise();
            return false;
        }

        return Apply(p => SetNumeric(p, field, value));
    }

    // ---- Reset ----------------------------------------------------------------------

    public bool HasOverride(EditField field)
    {
        var p = _history.Current;
        if (p is null) return false;

        return field switch
        {
            EditField.Title => p.Title is not null,
            EditField.Subtitle => p.Subtitle is not null,
            EditField.Caption => p.Caption is not null,
            EditField.XAxisTitle => p.XAxisTitle is not null,
            EditField.YAxisTitle => p.YAxisTitle is not null,
            EditField.Theme => p.ThemeId is not null,
            EditField.Palette => p.PaletteId is not null,
            EditField.ColorByCategory => p.ColorByCategory is not null,
            EditField.SeriesColor => p.SeriesColorHex is not null,
            EditField.BackgroundColor => p.BackgroundColorHex is not null,
            EditField.FontFamily => p.FontFamily is not null,
            EditField.TitleFontSize => p.TitleFontSize is not null,
            EditField.AxisFontSize => p.AxisFontSize is not null,
            EditField.TickFontSize => p.TickFontSize is not null,
            EditField.LineWidth => p.LineWidth is not null,
            EditField.MarkerSize => p.MarkerSize is not null,
            EditField.Opacity => p.Opacity is not null,
            EditField.ShowGrid => p.ShowGrid is not null,
            EditField.ShowLegend => p.ShowLegend is not null,
            EditField.ShowXAxis => p.ShowXAxis is not null,
            EditField.ShowYAxis => p.ShowYAxis is not null,
            EditField.Orientation => p.Orientation is not null,
            _ => false
        };
    }

    /// <summary>Reset one field to the recommendation/theme default. One history step.</summary>
    public void ResetField(EditField field) => Apply(p => ClearField(p, field));

    /// <summary>Reset every field in a section. One history step, so one Undo restores it —
    /// resetting a section then losing all of it to granular undo would be hostile.</summary>
    public void ResetSection(EditSection section)
    {
        Apply(p =>
        {
            foreach (var field in FieldsOf(section)) ClearField(p, field);
        });
    }

    /// <summary>Back to the recommendation exactly. One history step; undoable.</summary>
    public void ResetAll()
    {
        LastErrors = Array.Empty<PatchValidationError>();
        if (_history.Commit(null)) Raise();
    }

    // ---------------------------------------------------------------------------------

    private bool Apply(Action<FigurePatch> mutate)
    {
        var next = _history.Current?.Clone() ?? new FigurePatch();
        mutate(next);

        var errors = FigurePatchValidator.Validate(next);
        if (errors.Count > 0)
        {
            LastErrors = errors;
            Raise();
            return false;
        }

        LastErrors = Array.Empty<PatchValidationError>();
        if (_history.Commit(next)) Raise();
        return true;
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    private static string? OverrideOrNull(string? value, string defaultValue)
    {
        if (value is null) return null;
        string trimmed = value.Trim();
        if (trimmed.Length == 0) return null;
        return string.Equals(trimmed, defaultValue, StringComparison.Ordinal) ? null : trimmed;
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void SetNumeric(FigurePatch p, EditField field, double? value)
    {
        switch (field)
        {
            case EditField.TitleFontSize: p.TitleFontSize = value; break;
            case EditField.AxisFontSize: p.AxisFontSize = value; break;
            case EditField.TickFontSize: p.TickFontSize = value; break;
            case EditField.LineWidth: p.LineWidth = value; break;
            case EditField.MarkerSize: p.MarkerSize = value; break;
            case EditField.Opacity: p.Opacity = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(field), field, "Not a numeric field.");
        }
    }

    private static void ClearField(FigurePatch p, EditField field)
    {
        switch (field)
        {
            case EditField.Title: p.Title = null; break;
            case EditField.Subtitle: p.Subtitle = null; break;
            case EditField.Caption: p.Caption = null; break;
            case EditField.XAxisTitle: p.XAxisTitle = null; break;
            case EditField.YAxisTitle: p.YAxisTitle = null; break;
            case EditField.Theme: p.ThemeId = null; break;
            case EditField.Palette: p.PaletteId = null; break;
            case EditField.ColorByCategory: p.ColorByCategory = null; break;
            case EditField.SeriesColor: p.SeriesColorHex = null; break;
            case EditField.BackgroundColor: p.BackgroundColorHex = null; break;
            case EditField.FontFamily: p.FontFamily = null; break;
            case EditField.TitleFontSize: p.TitleFontSize = null; break;
            case EditField.AxisFontSize: p.AxisFontSize = null; break;
            case EditField.TickFontSize: p.TickFontSize = null; break;
            case EditField.LineWidth: p.LineWidth = null; break;
            case EditField.MarkerSize: p.MarkerSize = null; break;
            case EditField.Opacity: p.Opacity = null; break;
            case EditField.ShowGrid: p.ShowGrid = null; break;
            case EditField.ShowLegend: p.ShowLegend = null; break;
            case EditField.ShowXAxis: p.ShowXAxis = null; break;
            case EditField.ShowYAxis: p.ShowYAxis = null; break;
            case EditField.Orientation: p.Orientation = null; break;
        }
    }

    private static IEnumerable<EditField> FieldsOf(EditSection section) => section switch
    {
        EditSection.Text => new[]
        {
            EditField.Title, EditField.Subtitle, EditField.Caption,
            EditField.XAxisTitle, EditField.YAxisTitle
        },
        EditSection.Appearance => new[]
        {
            EditField.Theme, EditField.Palette, EditField.ColorByCategory,
            EditField.SeriesColor, EditField.BackgroundColor, EditField.FontFamily,
            EditField.TitleFontSize, EditField.AxisFontSize, EditField.TickFontSize,
            EditField.LineWidth, EditField.MarkerSize, EditField.Opacity
        },
        EditSection.Layout => new[]
        {
            EditField.ShowGrid, EditField.ShowLegend,
            EditField.ShowXAxis, EditField.ShowYAxis, EditField.Orientation
        },
        _ => Array.Empty<EditField>()
    };

    private static string FieldLabel(EditField field) => field switch
    {
        EditField.TitleFontSize => "Title size",
        EditField.AxisFontSize => "Axis size",
        EditField.TickFontSize => "Tick size",
        EditField.LineWidth => "Line width",
        EditField.MarkerSize => "Marker size",
        EditField.Opacity => "Opacity",
        _ => field.ToString()
    };
}
