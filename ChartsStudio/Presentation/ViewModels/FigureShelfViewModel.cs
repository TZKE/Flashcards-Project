using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIFlashcardMaker.ChartsStudio.Application.Rendering;
using AIFlashcardMaker.ChartsStudio.Application.Session;
using AIFlashcardMaker.ChartsStudio.Domain.ChartTypes;
using AIFlashcardMaker.ChartsStudio.Domain.Context;
using AIFlashcardMaker.ChartsStudio.Domain.Specs;
using AIFlashcardMaker.ChartsStudio.Domain.Themes;

namespace AIFlashcardMaker.ChartsStudio.Presentation.ViewModels;

/// <summary>How the shelf is ordered. Manual is the persisted, drag-reorderable order.</summary>
public enum ShelfSort { Manual, Title, Newest, RecentlyEdited, ChartType }

/// <summary>One kept figure as the shelf displays it.</summary>
public sealed class ShelfItemViewModel : ObservableObject
{
    private BitmapSource? _image;
    private bool _isSelected;
    private bool _isDropTarget;

    public ShelfItemViewModel(KeptFigure figure)
    {
        Figure = figure ?? throw new ArgumentNullException(nameof(figure));
        Style = FigureStyleResolver.Resolve(figure.Spec, figure.Patch);
    }

    public KeptFigure Figure { get; }

    /// <summary>Resolved once per refresh — the thumbnail and the texts must agree, and both
    /// must reflect the PATCHED figure, never the bare recommendation.</summary>
    public ResolvedFigureStyle Style { get; }

    public string Id => Figure.Id;
    public string Title => Style.Title;
    public string ChartTypeName => ChartTypeRegistry.Find(Figure.Spec.ChartTypeId)?.DisplayName ?? Figure.Spec.ChartTypeId;

    public string CreatedDisplay => Figure.CreatedAt.ToLocalTime().ToString("d MMM yyyy");
    public string EditedDisplay => Figure.LastEditedAt?.ToLocalTime().ToString("d MMM · HH:mm") ?? "";
    public bool HasBeenEdited => Figure.LastEditedAt is not null;

    /// <summary>The Modified badge: the user has styled this figure beyond its recommendation.</summary>
    public bool IsModified => Figure.IsModified;

    /// <summary>Reserved badges — persisted fields with no UI to set them yet.</summary>
    public bool IsFavorite => Figure.IsFavorite;
    public bool HasNotes => !string.IsNullOrWhiteSpace(Figure.Notes);

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>Highlight while a dragged figure would drop in front of this one.</summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set => Set(ref _isDropTarget, value);
    }

    public BitmapSource? Image
    {
        get => _image;
        private set => Set(ref _image, value);
    }

    public bool HasImage => _image is not null;

    public void SetImage(byte[] png)
    {
        try
        {
            var bitmap = new BitmapImage();
            using (var stream = new MemoryStream(png))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
            }
            bitmap.Freeze();
            Image = bitmap;
            OnPropertyChanged(nameof(HasImage));
        }
        catch { /* keep the placeholder; a bad thumbnail must not break the shelf */ }
    }
}

/// <summary>
/// Charts Studio Phase 4 — the Figure Shelf: the user's curated set.
///
/// The shelf displays KeptFigure records and mutates them ONLY through the session, which owns
/// persistence. Sorting and filtering are views over the session's list; Manual order is the
/// one persisted, so switching to Title order and back loses nothing.
///
/// SELECTION lives in ShelfSelectionModel — pure list logic, headless-testable — with the
/// ListBox's native Extended mode mapped onto it by the view. Bulk actions iterate the
/// selection and call session operations one figure at a time; there is no bulk path in
/// persistence to get out of sync with the individual one.
/// </summary>
public sealed class FigureShelfViewModel : ObservableObject
{
    private readonly ChartsStudioSession _session;
    private readonly FigureRenderQueue _renderQueue;
    private readonly Dispatcher _dispatcher;

    private AnalysisContext _context = AnalysisContext.None;
    private ShelfSort _sort = ShelfSort.Manual;
    private string _search = "";
    private int _refreshGeneration;

    private const int ThumbWidth = 520;
    private const int ThumbHeight = 300;
    private const double ThumbScale = 1.55;

    public FigureShelfViewModel(
        ChartsStudioSession session,
        FigureRenderQueue renderQueue,
        Dispatcher dispatcher)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _renderQueue = renderQueue ?? throw new ArgumentNullException(nameof(renderQueue));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public ObservableCollection<ShelfItemViewModel> Items { get; } = new();

    /// <summary>Raised when the user asks to edit a figure from the shelf.</summary>
    public event EventHandler<ShelfItemViewModel>? EditRequested;

    /// <summary>Phase 5 — raised with the figures to export (selection, or the whole set).</summary>
    public event EventHandler<IReadOnlyList<KeptFigure>>? ExportRequested;

    // ---- Header / organisation ------------------------------------------------------

    public IReadOnlyList<NamedOption> SortOptions { get; } = new List<NamedOption>
    {
        new() { Id = nameof(ShelfSort.Manual), Name = "My order" },
        new() { Id = nameof(ShelfSort.Title), Name = "Title" },
        new() { Id = nameof(ShelfSort.Newest), Name = "Newest first" },
        new() { Id = nameof(ShelfSort.RecentlyEdited), Name = "Recently edited" },
        new() { Id = nameof(ShelfSort.ChartType), Name = "Chart type" },
    };

    public NamedOption? SelectedSort
    {
        get => SortOptions.First(o => o.Id == _sort.ToString());
        set
        {
            if (value is null) return;
            _sort = Enum.Parse<ShelfSort>(value.Id);
            OnPropertyChanged(nameof(SelectedSort));
            OnPropertyChanged(nameof(CanReorder));
            Refresh();
        }
    }

    /// <summary>Drag-reorder only makes sense in the order that gets persisted.</summary>
    public bool CanReorder => _sort == ShelfSort.Manual;

    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value ?? "")) Refresh(); }
    }

    public int Count => _session.KeptFigureCount;

    public string CountLabel => Count == 1 ? "Shelf · 1 figure" : $"Shelf · {Count} figures";

    public bool IsEmpty => Items.Count == 0;

    public string EmptyReason =>
        _session.KeptFigureCount == 0
            ? "No figures kept yet. Keep figures from the proposed sheet and they will collect here as your figure set."
            : "No kept figure matches your search.";

    // ---- Selection ------------------------------------------------------------------

    public int SelectedCount => Items.Count(i => i.IsSelected);
    public bool HasSelection => SelectedCount > 0;
    public string SelectionLabel => SelectedCount == 1 ? "1 selected" : $"{SelectedCount} selected";

    /// <summary>Export is available whenever the shelf has any figures — no selection means
    /// "export everything", the common case for assembling a submission.</summary>
    public bool CanExport => Items.Count > 0;

    public string ExportButtonLabel => HasSelection ? $"Export {SelectedCount} selected" : "Export all";

    /// <summary>Called by the view whenever the ListBox's selection changes.</summary>
    public void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionLabel));
        OnPropertyChanged(nameof(ExportButtonLabel));
    }

    public void SelectAll()
    {
        foreach (var item in Items) item.IsSelected = true;
        NotifySelectionChanged();
    }

    public void ClearSelection()
    {
        foreach (var item in Items) item.IsSelected = false;
        NotifySelectionChanged();
    }

    // ---- Bulk actions ---------------------------------------------------------------

    /// <summary>Removes the selected figures from the shelf. Recommendations are untouched —
    /// the same figures reappear as proposals, un-kept, exactly as the architecture demands.</summary>
    public void DeleteSelected()
    {
        foreach (var item in Items.Where(i => i.IsSelected).ToList())
            _session.RemoveFigure(item.Id);
        Refresh();
    }

    public void DuplicateSelected()
    {
        foreach (var item in Items.Where(i => i.IsSelected).ToList())
            _session.DuplicateFigure(item.Id);
        Refresh();
    }

    public void RequestEdit(ShelfItemViewModel item)
    {
        if (item is not null) EditRequested?.Invoke(this, item);
    }

    /// <summary>Exports the current selection, or the entire shelf when nothing is selected.
    /// Figures are taken from the SESSION (patched, in persisted order), never the view rows.</summary>
    public void RequestExport()
    {
        var ids = Items.Where(i => i.IsSelected).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);

        var figures = ids.Count > 0
            ? _session.KeptFigures.Where(f => ids.Contains(f.Id)).ToList()
            : _session.KeptFigures.ToList();

        if (figures.Count > 0) ExportRequested?.Invoke(this, figures);
    }

    // ---- Reorder --------------------------------------------------------------------

    /// <summary>
    /// Drops the dragged figure at the target figure's position. Session persists; the shelf
    /// re-reads, so the UI can never show an order the file does not contain.
    /// </summary>
    public void ReorderTo(string draggedId, string targetId)
    {
        if (!CanReorder || draggedId == targetId) return;

        int targetIndex = -1;
        var kept = _session.KeptFigures;
        for (int i = 0; i < kept.Count; i++)
            if (kept[i].Id == targetId) { targetIndex = i; break; }

        if (targetIndex < 0) return;

        _session.ReorderFigure(draggedId, targetIndex);
        Refresh();
    }

    public void ClearDropTargets()
    {
        foreach (var item in Items) item.IsDropTarget = false;
    }

    // ---- Refresh --------------------------------------------------------------------

    /// <summary>
    /// Rebuilds the shelf from the session. Selection survives by id; thumbnails come back
    /// instantly for unchanged figures because the render cache already holds their styles.
    /// </summary>
    public void Refresh(AnalysisContext? context = null)
    {
        if (context is not null) _context = context;

        var selectedIds = Items.Where(i => i.IsSelected).Select(i => i.Id)
            .ToHashSet(StringComparer.Ordinal);

        var figures = ApplyOrganisation(_session.KeptFigures);

        Items.Clear();
        foreach (var figure in figures)
        {
            var item = new ShelfItemViewModel(figure) { IsSelected = selectedIds.Contains(figure.Id) };
            Items.Add(item);
        }

        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(CountLabel));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyReason));
        OnPropertyChanged(nameof(CanExport));
        NotifySelectionChanged();

        _ = RenderThumbnailsAsync();
    }

    /// <summary>Pure organisation pipeline — sort + search over the session's list. Static and
    /// side-effect-free so the QA harness can pin its behaviour without a dispatcher.</summary>
    public static IReadOnlyList<KeptFigure> Organise(
        IReadOnlyList<KeptFigure> figures, ShelfSort sort, string search)
    {
        IEnumerable<KeptFigure> result = figures;

        if (!string.IsNullOrWhiteSpace(search))
        {
            string q = search.Trim();
            result = result.Where(f =>
            {
                var style = FigureStyleResolver.Resolve(f.Spec, f.Patch);
                string typeName = ChartTypeRegistry.Find(f.Spec.ChartTypeId)?.DisplayName ?? "";
                return style.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || typeName.Contains(q, StringComparison.OrdinalIgnoreCase);
            });
        }

        result = sort switch
        {
            ShelfSort.Title => result.OrderBy(
                f => FigureStyleResolver.Resolve(f.Spec, f.Patch).Title, StringComparer.OrdinalIgnoreCase),
            ShelfSort.Newest => result.OrderByDescending(f => f.CreatedAt),
            ShelfSort.RecentlyEdited => result
                .OrderByDescending(f => f.LastEditedAt ?? DateTime.MinValue)
                .ThenByDescending(f => f.CreatedAt),
            ShelfSort.ChartType => result.OrderBy(f => f.Spec.ChartTypeId, StringComparer.Ordinal),
            _ => result   // Manual: the session's (persisted) order
        };

        return result.ToList();
    }

    private IReadOnlyList<KeptFigure> ApplyOrganisation(IReadOnlyList<KeptFigure> figures) =>
        Organise(figures, _sort, _search);

    private async Task RenderThumbnailsAsync()
    {
        if (!_context.IsLoaded) return;

        int generation = ++_refreshGeneration;

        foreach (var item in Items.ToList())
        {
            if (generation != _refreshGeneration) return;   // superseded by a newer refresh

            var request = new RenderRequest
            {
                Spec = item.Figure.Spec,
                Context = _context,
                Style = item.Style,
                WidthPixels = ThumbWidth,
                HeightPixels = ThumbHeight,
                ScaleFactor = ThumbScale
            };

            var result = await _renderQueue.RenderAsync(request).ConfigureAwait(true);
            if (generation != _refreshGeneration) return;

            if (result.Succeeded && result.PngBytes is not null)
            {
                if (_dispatcher.CheckAccess()) item.SetImage(result.PngBytes);
                else _dispatcher.Invoke(() => item.SetImage(result.PngBytes));
            }
        }
    }
}
