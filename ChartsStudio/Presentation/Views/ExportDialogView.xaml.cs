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
}
