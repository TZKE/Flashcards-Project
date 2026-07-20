using System.Windows;
using System.Windows.Controls;
using AIFlashcardMaker.ChartsStudio.Domain.Projects;
using AIFlashcardMaker.ChartsStudio.Presentation.ViewModels;

namespace AIFlashcardMaker.ChartsStudio.Presentation.Views;

/// <summary>
/// Charts Studio Phase 1 — Project Picker view.
///
/// Card activation is handled here rather than through a command because the app takes no MVVM
/// toolkit dependency (see ObservableObject). The handler does one thing: resolve which card
/// was activated and hand it to the view model. No decision is made in this file.
/// </summary>
public partial class ProjectPickerView : UserControl
{
    public ProjectPickerView()
    {
        InitializeComponent();
    }

    private void ProjectCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        if (element.DataContext is not ProjectSummary summary) return;

        (DataContext as ProjectPickerViewModel)?.Choose(summary);
    }
}
