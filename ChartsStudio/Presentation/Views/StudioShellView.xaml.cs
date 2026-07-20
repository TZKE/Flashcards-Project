using System.Windows;
using System.Windows.Controls;
using AIFlashcardMaker.ChartsStudio.Presentation.ViewModels;

namespace AIFlashcardMaker.ChartsStudio.Presentation.Views;

/// <summary>
/// Charts Studio Phase 1 — studio shell view.
///
/// Layout only. The one interaction wired here reports intent to the view model and decides
/// nothing itself.
/// </summary>
public partial class StudioShellView : UserControl
{
    public StudioShellView()
    {
        InitializeComponent();
    }

    private void ChangeProject_Click(object sender, RoutedEventArgs e) =>
        (DataContext as StudioShellViewModel)?.RequestChangeProject();

    private void ShowSheet_Click(object sender, RoutedEventArgs e) =>
        (DataContext as StudioShellViewModel)?.ShowSheet();

    private void ShowShelf_Click(object sender, RoutedEventArgs e) =>
        (DataContext as StudioShellViewModel)?.ShowShelf();
}
