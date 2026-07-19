using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIFlashcardMaker.ChartsStudio.Presentation.ViewModels;

namespace AIFlashcardMaker.ChartsStudio.Presentation.Views;

/// <summary>
/// Charts Studio Phase 4 — Figure Shelf view.
///
/// The ListBox supplies Extended selection (single / Ctrl / Shift / Ctrl+A) natively; this
/// code-behind syncs it to the view model, forwards actions, and drives drag-reorder. Reorder
/// decisions — whether it is allowed, where the figure lands, persistence — all live in the
/// view model and session; this file only reports "the user dropped A on B".
/// </summary>
public partial class FigureShelfView : UserControl
{
    private Point _dragStart;
    private ShelfItemViewModel? _dragCandidate;

    public FigureShelfView()
    {
        InitializeComponent();
        PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(this);
    }

    private FigureShelfViewModel? ViewModel => DataContext as FigureShelfViewModel;

    private static ShelfItemViewModel? ItemOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as ShelfItemViewModel;

    // ---- Selection ------------------------------------------------------------------

    private void ShelfList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel?.NotifySelectionChanged();

    private void ShelfList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && ViewModel?.HasSelection == true)
        {
            ViewModel.DeleteSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ViewModel?.ClearSelection();
            e.Handled = true;
        }
        // Ctrl+A is ListBox-native with SelectionMode=Extended.
    }

    // ---- Actions --------------------------------------------------------------------

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel?.RequestEdit(item);
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e) => ViewModel?.DuplicateSelected();

    private void Delete_Click(object sender, RoutedEventArgs e) => ViewModel?.DeleteSelected();

    private void Export_Click(object sender, RoutedEventArgs e) => ViewModel?.RequestExport();

    // ---- Drag reorder ---------------------------------------------------------------
    //
    // Standard WPF drag: press on a card, move past the system threshold, DoDragDrop with the
    // figure id. Dropping before another card asks the view model to reorder; everything else
    // is highlight bookkeeping.

    private void Item_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { _dragCandidate = null; return; }
        if (ViewModel is not { CanReorder: true }) return;

        var item = ItemOf(sender);
        if (item is null) return;

        var position = e.GetPosition(this);
        if (_dragCandidate is null)
        {
            if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            _dragCandidate = item;
            DragDrop.DoDragDrop((DependencyObject)sender, item.Id, DragDropEffects.Move);
            _dragCandidate = null;
            ViewModel?.ClearDropTargets();
        }
    }

    private void Item_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.Move;
        e.Handled = true;

        if (ItemOf(sender) is { } item && ViewModel is not null)
        {
            ViewModel.ClearDropTargets();
            if (!Equals(item, _dragCandidate)) item.IsDropTarget = true;
        }
    }

    private void Item_DragLeave(object sender, DragEventArgs e)
    {
        if (ItemOf(sender) is { } item) item.IsDropTarget = false;
    }

    private void Item_Drop(object sender, DragEventArgs e)
    {
        if (ItemOf(sender) is not { } target || ViewModel is null) return;
        if (e.Data.GetData(typeof(string)) is not string draggedId) return;

        ViewModel.ClearDropTargets();
        ViewModel.ReorderTo(draggedId, target.Id);
        e.Handled = true;
    }
}
