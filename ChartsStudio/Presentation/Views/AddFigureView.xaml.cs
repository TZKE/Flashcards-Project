using System.Windows;
using System.Windows.Controls;
using AIFlashcardMaker.ChartsStudio.Presentation.ViewModels;

namespace AIFlashcardMaker.ChartsStudio.Presentation.Views;

/// <summary>
/// Charts Studio Phase 2 — Add Figure view.
///
/// Resolves which option was activated and forwards it. Decisions belong to the view model.
/// </summary>
public partial class AddFigureView : UserControl
{
    public AddFigureView()
    {
        InitializeComponent();
    }

    private AddFigureViewModel? ViewModel => DataContext as AddFigureViewModel;

    private void Option_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AddFigureOption option)
            ViewModel?.Choose(option);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => ViewModel?.Close();
}
