using System.Windows;
using System.Windows.Controls;
using AIFlashcardMaker.ChartsStudio.Presentation.ViewModels;

namespace AIFlashcardMaker.ChartsStudio.Presentation.Views;

/// <summary>
/// Charts Studio Phase 5 — Export dialog view. Forwards gestures to the view model, which owns
/// every decision. The export itself runs on the service, off the UI thread.
/// </summary>
public partial class ExportDialogView : UserControl
{
    public ExportDialogView()
    {
        InitializeComponent();
    }

    private ExportDialogViewModel? ViewModel => DataContext as ExportDialogViewModel;

    private void Browse_Click(object sender, RoutedEventArgs e) => ViewModel?.BrowseDestination();

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.ExportAsync();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => ViewModel?.Cancel();

    private void Close_Click(object sender, RoutedEventArgs e) => ViewModel?.Close();

    private void Root_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) Root.Focus();
    }

    // Escape closes the dialog — but Close() itself ignores the request while an export is
    // running (that must be cancelled, not dismissed), so this is safe mid-export.
    private void Root_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape) { ViewModel?.Close(); e.Handled = true; }
    }
}
