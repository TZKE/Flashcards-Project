using System.Windows;
using System.Windows.Controls;
using AIFlashcardMaker.ChartsStudio.Presentation.ViewModels;

namespace AIFlashcardMaker.ChartsStudio.Presentation.Views;

/// <summary>
/// Charts Studio Phase 1 — module host.
///
/// The single entry surface MainWindow embeds. Its code-behind is deliberately almost empty:
/// it forwards two gestures to the view model and holds no state of its own. Anything that
/// grows here later belongs in a view model instead — that discipline is what stops this
/// module drifting toward the code-behind-heavy shape the rest of the app has.
/// </summary>
public partial class ChartsStudioHostView : UserControl
{
    public ChartsStudioHostView()
    {
        InitializeComponent();
    }

    private ChartsStudioViewModel? ViewModel => DataContext as ChartsStudioViewModel;

    /// <summary>
    /// Called by the host every time the user navigates to Charts Studio. Re-entrant by
    /// design: entering an already-open studio keeps the user where they were.
    /// </summary>
    public void Enter() => ViewModel?.Enter();

    private void ChangeProject_Click(object sender, RoutedEventArgs e) => ViewModel?.ChangeProject();
}
