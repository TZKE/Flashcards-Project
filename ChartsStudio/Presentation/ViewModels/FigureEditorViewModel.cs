using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIFlashcardMaker.ChartsStudio.Application.Figures;
using AIFlashcardMaker.ChartsStudio.Application.Rendering;
using AIFlashcardMaker.ChartsStudio.Application.Session;
using AIFlashcardMaker.ChartsStudio.Domain.Context;
using AIFlashcardMaker.ChartsStudio.Domain.Specs;
using AIFlashcardMaker.ChartsStudio.Domain.Themes;

namespace AIFlashcardMaker.ChartsStudio.Presentation.ViewModels;

/// <summary>A pickable option for theme / palette / font combos.</summary>
public sealed class NamedOption
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public override string ToString() => Name;
}

/// <summary>
/// Charts Studio Phase 3 — the Figure Editor.
///
/// A THIN BINDING SHIM. Every rule lives in FigureEditSession (Application, headless,
/// exhaustively unit-tested); this class translates between WPF bindings and that session, and
/// owns exactly two UI concerns: marshalling preview renders onto the dispatcher, and the
/// discard-confirmation flow. If a behaviour looks wrong in the editor, debug the session
/// first — this file has almost no behaviour to be wrong in.
///
/// LIVE PREVIEW, LATEST-WINS: every accepted edit requests a render. Stale results are
/// discarded by generation number rather than blocking — the user can type ahead of the
/// renderer and the preview always converges on the newest state. The render cache makes
/// undo/redo previews instant, because their styles were already drawn once.
/// </summary>
public sealed class FigureEditorViewModel : ObservableObject
{
    private readonly ChartsStudioSession _session;
    private readonly FigureRenderQueue _renderQueue;
    private readonly Dispatcher _dispatcher;

    private FigureEditSession? _edit;
    private AnalysisContext _context = AnalysisContext.None;
    private KeptFigure? _figure;

    private bool _isOpen;
    private bool _isConfirmingDiscard;
    private BitmapSource? _previewImage;
    private bool _isPreviewStale;
    private int _previewGeneration;

    // Editor preview = the canonical export canvas (672×426 logical) at 1.5× supersampling.
    // Phase 5 made this exact rather than approximate: the editor is the surface the user
    // APPROVES, so it must share the export's logical geometry to the unit — same canvas,
    // same tick layout, different magnification only. See ExportCanvas.
    private const int PreviewWidth = Domain.Export.ExportCanvas.LogicalWidth * 3 / 2;    // 1008
    private const int PreviewHeight = Domain.Export.ExportCanvas.LogicalHeight * 3 / 2;  // 639
    private const double PreviewScale = 1.5;

    public FigureEditorViewModel(
        ChartsStudioSession session,
        FigureRenderQueue renderQueue,
        Dispatcher dispatcher)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _renderQueue = renderQueue ?? throw new ArgumentNullException(nameof(renderQueue));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        UndoCommand = new RelayCommand(Undo, () => _edit?.CanUndo == true);
        RedoCommand = new RelayCommand(Redo, () => _edit?.CanRedo == true);
        CloseCommand = new RelayCommand(RequestClose);
    }

    /// <summary>Raised after a successful save, so the shelf can refresh its thumbnails.</summary>
    public event EventHandler? Saved;

    /// <summary>Phase 5 — raised to export the figure currently being edited.</summary>
    public event EventHandler<KeptFigure>? ExportRequested;

    /// <summary>Exports this figure. Saves first if dirty, so the export is of what the user
    /// sees — exporting stale styling would violate the WYSIWYG promise at the worst moment.</summary>
    public void RequestExport()
    {
        if (_figure is null) return;
        if (IsDirty) Save();
        ExportRequested?.Invoke(this, _figure);
    }

    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand CloseCommand { get; }

    // ---- Lifecycle ------------------------------------------------------------------

    public bool IsOpen
    {
        get => _isOpen;
        private set => Set(ref _isOpen, value);
    }

    /// <summary>The inline "discard your edits?" panel. Modal within the editor overlay.</summary>
    public bool IsConfirmingDiscard
    {
        get => _isConfirmingDiscard;
        private set => Set(ref _isConfirmingDiscard, value);
    }

    public void Open(KeptFigure figure, AnalysisContext context)
    {
        _figure = figure ?? throw new ArgumentNullException(nameof(figure));
        _context = context ?? AnalysisContext.None;

        if (_edit is not null) _edit.Changed -= OnEditChanged;
        _edit = new FigureEditSession(figure);
        _edit.Changed += OnEditChanged;

        IsConfirmingDiscard = false;
        IsOpen = true;

        RaiseEverything();
        _ = RenderPreviewAsync();
    }

    /// <summary>Close button / Esc. Dirty edits get a confirmation instead of vanishing.</summary>
    public void RequestClose()
    {
        if (_edit is null) { IsOpen = false; return; }

        if (_edit.IsDirty) IsConfirmingDiscard = true;
        else CloseNow();
    }

    public void ConfirmDiscard() => CloseNow();

    public void CancelDiscard() => IsConfirmingDiscard = false;

    /// <summary>Save from the confirmation panel: persist, then close.</summary>
    public void SaveAndClose()
    {
        Save();
        CloseNow();
    }

    private void CloseNow()
    {
        IsConfirmingDiscard = false;
        IsOpen = false;
        _previewImage = null;
        OnPropertyChanged(nameof(PreviewImage));
    }

    public void Save()
    {
        if (_edit is null || _figure is null) return;

        var patch = _edit.MarkSaved();
        _session.UpdatePatch(_figure.Id, patch);
        Saved?.Invoke(this, EventArgs.Empty);
        RaiseEverything();
    }

    // ---- History --------------------------------------------------------------------

    public bool CanUndo => _edit?.CanUndo == true;
    public bool CanRedo => _edit?.CanRedo == true;
    public bool IsDirty => _edit?.IsDirty == true;
    public bool CanSave => IsDirty;

    public void Undo() => _edit?.Undo();
    public void Redo() => _edit?.Redo();

    public void ResetAll() => _edit?.ResetAll();
    public void ResetSection(EditSection section) => _edit?.ResetSection(section);
    public void ResetField(EditField field) => _edit?.ResetField(field);

    // ---- Header ---------------------------------------------------------------------

    public string HeaderTitle => _figure is null ? "" : $"Edit figure — {_edit?.CurrentStyle.Title}";

    public string ChartTypeName => _edit?.ChartType?.DisplayName ?? "";

    // ---- Preview --------------------------------------------------------------------

    public BitmapSource? PreviewImage
    {
        get => _previewImage;
        private set => Set(ref _previewImage, value);
    }

    /// <summary>True while the preview lags the newest edit — shows a subtle "updating…".</summary>
    public bool IsPreviewStale
    {
        get => _isPreviewStale;
        private set => Set(ref _isPreviewStale, value);
    }

    public string CaptionPreview => _edit?.CurrentStyle.Caption ?? "";
    public bool HasCaption => !string.IsNullOrWhiteSpace(CaptionPreview);

    // ---- Validation -----------------------------------------------------------------

    public string ValidationText =>
        _edit is null ? "" : string.Join("   ·   ", _edit.LastErrors.Select(e => e.ToString()));

    public bool HasValidationErrors => _edit is { LastErrors.Count: > 0 };

    // ---- Text fields ----------------------------------------------------------------
    //
    // Getters show the EFFECTIVE value (override ?? default) so the user edits what they see;
    // setters push through the edit session, which normalises "typed the default back" to a
    // cleared override. HasXxxOverride drives the per-field reset buttons.

    public string TitleText
    {
        get => _edit?.CurrentStyle.Title ?? "";
        set => _edit?.SetTitle(value);
    }

    public string SubtitleText
    {
        get => _edit?.CurrentStyle.Subtitle ?? "";
        set => _edit?.SetSubtitle(value);
    }

    public string CaptionText
    {
        get => _edit?.CurrentStyle.Caption ?? "";
        set => _edit?.SetCaption(value);
    }

    public string XAxisTitleText
    {
        get => _edit?.CurrentStyle.XLabel ?? "";
        set => _edit?.SetXAxisTitle(value);
    }

    public string YAxisTitleText
    {
        get => _edit?.CurrentStyle.YLabel ?? "";
        set => _edit?.SetYAxisTitle(value);
    }

    public bool HasTitleOverride => _edit?.HasOverride(EditField.Title) == true;
    public bool HasSubtitleOverride => _edit?.HasOverride(EditField.Subtitle) == true;
    public bool HasCaptionOverride => _edit?.HasOverride(EditField.Caption) == true;
    public bool HasXAxisOverride => _edit?.HasOverride(EditField.XAxisTitle) == true;
    public bool HasYAxisOverride => _edit?.HasOverride(EditField.YAxisTitle) == true;

    // ---- Appearance -----------------------------------------------------------------

    public IReadOnlyList<NamedOption> ThemeOptions { get; } =
        FigureThemes.All.Select(t => new NamedOption { Id = t.Id, Name = t.DisplayName }).ToList();

    public IReadOnlyList<NamedOption> PaletteOptions { get; } =
        FigureThemes.AllPalettes.Select(p => new NamedOption { Id = p.Id, Name = p.DisplayName }).ToList();

    public IReadOnlyList<NamedOption> FontOptions { get; } = new List<NamedOption>
    {
        new() { Id = "Segoe UI", Name = "Segoe UI" },
        new() { Id = "Arial", Name = "Arial" },
        new() { Id = "Calibri", Name = "Calibri" },
        new() { Id = "Times New Roman", Name = "Times New Roman" },
        new() { Id = "Georgia", Name = "Georgia" },
    };

    /// <summary>Swatches offered for the series colour — the active palette, so one-click
    /// recolouring stays inside a coherent set. A hex box covers everything else.</summary>
    public IReadOnlyList<string> ColorSwatches =>
        FigureThemes.GetPalette(_edit?.CurrentPatch?.PaletteId).Hexes;

    public NamedOption? SelectedTheme
    {
        get => ThemeOptions.FirstOrDefault(o => o.Id == (_edit?.CurrentPatch?.ThemeId ?? FigureThemes.DefaultThemeId));
        set { if (value is not null) _edit?.SetTheme(value.Id); }
    }

    public NamedOption? SelectedPalette
    {
        get => PaletteOptions.FirstOrDefault(o => o.Id == (_edit?.CurrentPatch?.PaletteId ?? FigureThemes.DefaultPaletteId));
        set { if (value is not null) _edit?.SetPalette(value.Id); }
    }

    public NamedOption? SelectedFont
    {
        get
        {
            string current = _edit?.CurrentStyle.FontFamily ?? "Segoe UI";
            return FontOptions.FirstOrDefault(o => o.Id == current)
                ?? FontOptions[0];
        }
        set { if (value is not null) _edit?.SetFontFamily(value.Id); }
    }

    public bool ColorByCategory
    {
        get => _edit?.CurrentPatch?.ColorByCategory == true;
        set => _edit?.SetColorByCategory(value);
    }

    public bool SupportsCategoryColors => _edit?.SupportsCategoryColors == true;

    public string SeriesColorHexText
    {
        get => _edit?.CurrentStyle.SeriesFillHex ?? "";
        set => _edit?.SetSeriesColor(value);
    }

    public void PickSeriesColor(string hex) => _edit?.SetSeriesColor(hex);

    public bool HasSeriesColorOverride => _edit?.HasOverride(EditField.SeriesColor) == true;

    // Numeric fields: bound as text, parsed and validated by the edit session. The getter
    // shows the effective value so a rejected entry visibly snaps back on refresh.

    public string TitleFontSizeText
    {
        get => FormatNumber(_edit?.CurrentStyle.TitleFontSize);
        set => _edit?.TrySetNumber(EditField.TitleFontSize, value);
    }

    public string AxisFontSizeText
    {
        get => FormatNumber(_edit?.CurrentStyle.AxisFontSize);
        set => _edit?.TrySetNumber(EditField.AxisFontSize, value);
    }

    public string TickFontSizeText
    {
        get => FormatNumber(_edit?.CurrentStyle.TickFontSize);
        set => _edit?.TrySetNumber(EditField.TickFontSize, value);
    }

    public string LineWidthText
    {
        get => FormatNumber(_edit?.CurrentStyle.LineWidth);
        set => _edit?.TrySetNumber(EditField.LineWidth, value);
    }

    public string MarkerSizeText
    {
        get => FormatNumber(_edit?.CurrentStyle.MarkerSize);
        set => _edit?.TrySetNumber(EditField.MarkerSize, value);
    }

    public string OpacityText
    {
        get => FormatNumber(_edit?.CurrentStyle.Opacity);
        set => _edit?.TrySetNumber(EditField.Opacity, value);
    }

    // ---- Layout ---------------------------------------------------------------------

    public bool ShowGrid
    {
        get => _edit?.CurrentStyle.ShowGrid ?? true;
        set => _edit?.SetShowGrid(value);
    }

    public bool ShowLegend
    {
        get => _edit?.CurrentStyle.ShowLegend ?? false;
        set => _edit?.SetShowLegend(value);
    }

    public bool ShowXAxis
    {
        get => _edit?.CurrentStyle.ShowXAxis ?? true;
        set => _edit?.SetShowXAxis(value);
    }

    public bool ShowYAxis
    {
        get => _edit?.CurrentStyle.ShowYAxis ?? true;
        set => _edit?.SetShowYAxis(value);
    }

    public bool SupportsOrientation => _edit?.SupportsOrientation == true;

    public bool IsHorizontal
    {
        get => _edit?.CurrentStyle.Horizontal ?? false;
        set => _edit?.SetOrientation(value ? "horizontal" : "vertical");
    }

    // ---------------------------------------------------------------------------------

    private void OnEditChanged(object? sender, EventArgs e)
    {
        RaiseEverything();
        _ = RenderPreviewAsync();
    }

    private async Task RenderPreviewAsync()
    {
        if (_edit is null || _figure is null || !_context.IsLoaded) return;

        int generation = ++_previewGeneration;
        IsPreviewStale = true;

        var request = new RenderRequest
        {
            Spec = _figure.Spec,
            Context = _context,
            Style = _edit.CurrentStyle,
            WidthPixels = PreviewWidth,
            HeightPixels = PreviewHeight,
            ScaleFactor = PreviewScale
        };

        var result = await _renderQueue.RenderAsync(request).ConfigureAwait(true);

        void Apply()
        {
            // Latest wins: a slower render for an older edit must never overwrite the preview
            // of a newer one. Superseded results are simply dropped.
            if (generation != _previewGeneration) return;

            if (result.Succeeded && result.PngBytes is not null)
            {
                try
                {
                    var bitmap = new BitmapImage();
                    using (var stream = new MemoryStream(result.PngBytes))
                    {
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = stream;
                        bitmap.EndInit();
                    }
                    bitmap.Freeze();
                    PreviewImage = bitmap;
                }
                catch
                {
                    // A failed decode keeps the previous preview — better stale than blank.
                }
            }

            IsPreviewStale = false;
        }

        if (_dispatcher.CheckAccess()) Apply();
        else _dispatcher.Invoke(Apply);
    }

    private static string FormatNumber(double? value) =>
        value is null ? "" : value.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private void RaiseEverything()
    {
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(ChartTypeName));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(ValidationText));
        OnPropertyChanged(nameof(HasValidationErrors));
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(SubtitleText));
        OnPropertyChanged(nameof(CaptionText));
        OnPropertyChanged(nameof(XAxisTitleText));
        OnPropertyChanged(nameof(YAxisTitleText));
        OnPropertyChanged(nameof(HasTitleOverride));
        OnPropertyChanged(nameof(HasSubtitleOverride));
        OnPropertyChanged(nameof(HasCaptionOverride));
        OnPropertyChanged(nameof(HasXAxisOverride));
        OnPropertyChanged(nameof(HasYAxisOverride));
        OnPropertyChanged(nameof(SelectedTheme));
        OnPropertyChanged(nameof(SelectedPalette));
        OnPropertyChanged(nameof(SelectedFont));
        OnPropertyChanged(nameof(ColorByCategory));
        OnPropertyChanged(nameof(SupportsCategoryColors));
        OnPropertyChanged(nameof(ColorSwatches));
        OnPropertyChanged(nameof(SeriesColorHexText));
        OnPropertyChanged(nameof(HasSeriesColorOverride));
        OnPropertyChanged(nameof(TitleFontSizeText));
        OnPropertyChanged(nameof(AxisFontSizeText));
        OnPropertyChanged(nameof(TickFontSizeText));
        OnPropertyChanged(nameof(LineWidthText));
        OnPropertyChanged(nameof(MarkerSizeText));
        OnPropertyChanged(nameof(OpacityText));
        OnPropertyChanged(nameof(ShowGrid));
        OnPropertyChanged(nameof(ShowLegend));
        OnPropertyChanged(nameof(ShowXAxis));
        OnPropertyChanged(nameof(ShowYAxis));
        OnPropertyChanged(nameof(SupportsOrientation));
        OnPropertyChanged(nameof(IsHorizontal));
        OnPropertyChanged(nameof(CaptionPreview));
        OnPropertyChanged(nameof(HasCaption));

        UndoCommand.RaiseCanExecuteChanged();
        RedoCommand.RaiseCanExecuteChanged();
    }
}
