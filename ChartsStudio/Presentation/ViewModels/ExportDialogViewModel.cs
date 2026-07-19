using System.IO;
using System.Windows.Threading;
using AIFlashcardMaker.ChartsStudio.Application.Export;
using AIFlashcardMaker.ChartsStudio.Application.Session;
using AIFlashcardMaker.ChartsStudio.Domain.Context;
using AIFlashcardMaker.ChartsStudio.Domain.Export;
using AIFlashcardMaker.ChartsStudio.Domain.Specs;

namespace AIFlashcardMaker.ChartsStudio.Presentation.ViewModels;

/// <summary>What the user asked to export when they opened the dialog.</summary>
public enum ExportScope { CurrentFigure, SelectedFigures, EntireShelf }

/// <summary>A togglable format choice bound to a checkbox.</summary>
public sealed class ExportFormatChoice : ObservableObject
{
    private bool _isSelected;
    public required ExportFormat Format { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

/// <summary>
/// Charts Studio Phase 5 — the export dialog's brain.
///
/// It gathers choices, ASSEMBLES an ExportPlan, and hands that plan to the headless
/// ExportService. It contains no export logic — no rendering, no encoding, no file writing —
/// only the UI concerns the service must not have: which figures the user picked, progress
/// display, and a file-picker callback. Everything it decides is visible in the plan before a
/// byte is written, which is exactly what the export preview renders from.
/// </summary>
public sealed class ExportDialogViewModel : ObservableObject
{
    private readonly ChartsStudioSession _session;
    private readonly ExportService _service;
    private readonly Dispatcher _dispatcher;
    private readonly Func<string?> _pickFolder;

    private IReadOnlyList<KeptFigure> _figures = Array.Empty<KeptFigure>();
    private AnalysisContext _context = AnalysisContext.None;
    private CancellationTokenSource? _cts;

    private bool _isOpen;
    private bool _isExporting;
    private double _progressFraction;
    private string _progressLabel = "";
    private string _statusMessage = "";
    private bool _showResult;
    private string _destination = "";

    public ExportDialogViewModel(
        ChartsStudioSession session,
        ExportService service,
        Dispatcher dispatcher,
        Func<string?> pickFolder)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _pickFolder = pickFolder ?? throw new ArgumentNullException(nameof(pickFolder));

        Formats = ExportFormatCatalog.Formats.Select(f => new ExportFormatChoice
        {
            Format = f.Format,
            DisplayName = f.DisplayName,
            Description = f.Description,
            IsSelected = f.Format == ExportFormat.Png   // PNG on by default
        }).ToList();

        foreach (var f in Formats)
            f.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ExportFormatChoice.IsSelected)) RaiseCanExport(); };
    }

    // ---- Options bound to the dialog -------------------------------------------------

    public IReadOnlyList<ExportProfile> Profiles { get; } = ExportProfiles.BuiltIn;
    public IReadOnlyList<ExportFormatChoice> Formats { get; }

    public IReadOnlyList<NamedOption> NameTemplates { get; } = new List<NamedOption>
    {
        new() { Id = nameof(ExportNameTemplate.FigureNumber), Name = "Figure 1, Figure 2 …" },
        new() { Id = nameof(ExportNameTemplate.Title), Name = "Figure title" },
        new() { Id = nameof(ExportNameTemplate.StudyFigureNumber), Name = "Study_Figure_1 …" },
        new() { Id = nameof(ExportNameTemplate.StudyTypeTitle), Name = "Study_ChartType_Title" },
    };

    private ExportProfile _selectedProfile = ExportProfiles.Journal;
    public ExportProfile SelectedProfile
    {
        get => _selectedProfile;
        set { if (Set(ref _selectedProfile, value)) OnPropertyChanged(nameof(ProfileSummary)); }
    }

    public string ProfileSummary =>
        $"{SelectedProfile.WidthInches:0.##} × {SelectedProfile.HeightInches:0.##} in  ·  " +
        $"{SelectedProfile.Dpi} DPI  ·  {SelectedProfile.PixelWidth} × {SelectedProfile.PixelHeight} px";

    private NamedOption? _selectedNameTemplate;
    public NamedOption? SelectedNameTemplate
    {
        get => _selectedNameTemplate ??= NameTemplates[0];
        set => Set(ref _selectedNameTemplate, value);
    }

    private bool _asPackage = true;
    public bool AsPackage { get => _asPackage; set => Set(ref _asPackage, value); }

    private bool _includeCaptions = true;
    public bool IncludeCaptions { get => _includeCaptions; set => Set(ref _includeCaptions, value); }

    private bool _includeManifest = true;
    public bool IncludeManifest { get => _includeManifest; set => Set(ref _includeManifest, value); }

    public string Destination
    {
        get => _destination;
        set { if (Set(ref _destination, value)) RaiseCanExport(); }
    }

    // ---- Lifecycle & state -----------------------------------------------------------

    public bool IsOpen { get => _isOpen; private set => Set(ref _isOpen, value); }
    public bool IsExporting { get => _isExporting; private set { if (Set(ref _isExporting, value)) RaiseCanExport(); } }
    public bool ShowResult { get => _showResult; private set => Set(ref _showResult, value); }

    public double ProgressFraction { get => _progressFraction; private set => Set(ref _progressFraction, value); }
    public string ProgressLabel { get => _progressLabel; private set => Set(ref _progressLabel, value); }
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }

    public string ScopeSummary { get; private set; } = "";

    public bool CanExport =>
        !IsExporting && _figures.Count > 0 &&
        Formats.Any(f => f.IsSelected) &&
        !string.IsNullOrWhiteSpace(Destination);

    private void RaiseCanExport()
    {
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(ScopeSummary));
    }

    /// <summary>Opens the dialog for a set of figures. Figures are captured at open time, so
    /// the export is of what the user chose, not whatever the shelf holds when it finishes.</summary>
    public void Open(IReadOnlyList<KeptFigure> figures, AnalysisContext context, ExportScope scope)
    {
        _figures = figures ?? Array.Empty<KeptFigure>();
        _context = context ?? AnalysisContext.None;

        ScopeSummary = _figures.Count switch
        {
            0 => "No figures to export.",
            1 => "Exporting 1 figure.",
            _ => $"Exporting {_figures.Count} figures."
        };

        if (string.IsNullOrWhiteSpace(Destination))
            Destination = DefaultDestination(_context);

        ShowResult = false;
        StatusMessage = "";
        ProgressFraction = 0;
        IsOpen = true;

        OnPropertyChanged(nameof(ScopeSummary));
        RaiseCanExport();
    }

    public void Close()
    {
        if (IsExporting) return;   // a running export must be cancelled, not dismissed
        IsOpen = false;
    }

    public void BrowseDestination()
    {
        string? chosen = _pickFolder();
        if (!string.IsNullOrWhiteSpace(chosen)) Destination = chosen;
    }

    public void Cancel() => _cts?.Cancel();

    // ---- Run -------------------------------------------------------------------------

    public async Task ExportAsync()
    {
        if (!CanExport) return;

        var plan = new ExportPlan
        {
            Figures = _figures,
            Profile = SelectedProfile,
            Formats = Formats.Where(f => f.IsSelected).Select(f => f.Format).ToList(),
            NameTemplate = Enum.Parse<ExportNameTemplate>(SelectedNameTemplate!.Id),
            DestinationDirectory = Destination.Trim(),
            StudyName = string.IsNullOrWhiteSpace(_context.ProjectTitle) ? "Study" : _context.ProjectTitle,
            ProjectId = _context.ProjectId,
            AsPackage = AsPackage,
            IncludeCaptions = AsPackage && IncludeCaptions,
            IncludeManifest = AsPackage && IncludeManifest
        };

        _cts = new CancellationTokenSource();
        IsExporting = true;
        ShowResult = false;
        ProgressFraction = 0;
        ProgressLabel = "Preparing…";

        var progress = new Progress<ExportProgress>(p =>
        {
            ProgressFraction = p.Total == 0 ? 0 : (double)p.Completed / p.Total;
            ProgressLabel = $"{p.Completed} / {p.Total}  ·  {p.CurrentLabel}";
        });

        try
        {
            var result = await _service.ExportAsync(plan, _context, progress, _cts.Token).ConfigureAwait(true);
            ShowRunResult(result);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed unexpectedly ({ex.GetType().Name}).";
            ShowResult = true;
        }
        finally
        {
            IsExporting = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private void ShowRunResult(ExportRunResult result)
    {
        if (result.WasCancelled)
            StatusMessage = $"Export cancelled. {result.SucceededCount} file(s) written before cancelling.";
        else if (result.AllSucceeded)
            StatusMessage = $"Exported {result.SucceededCount} file(s) to {Destination}.";
        else
        {
            var firstError = result.Items.FirstOrDefault(i => !i.Success)?.Error ?? "Unknown error.";
            StatusMessage = $"Exported {result.SucceededCount} file(s); {result.FailedCount} failed. First problem: {firstError}";
        }

        ProgressLabel = "";
        ShowResult = true;
    }

    private static string DefaultDestination(AnalysisContext context)
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string safeName = FileNameSanitizer.Sanitize(context.ProjectTitle, "Charts");
        return Path.Combine(documents, "OrbitLab Figures", safeName);
    }
}
