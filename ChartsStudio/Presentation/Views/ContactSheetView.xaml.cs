using System.Windows;
using System.Windows.Controls;
using AIFlashcardMaker.ChartsStudio.Presentation.ViewModels;

namespace AIFlashcardMaker.ChartsStudio.Presentation.Views;

/// <summary>
/// Charts Studio Phase 2 — Contact Sheet view.
///
/// Code-behind resolves which card a gesture landed on and forwards it. No decision is made
/// here — keep, remove and add all belong to the view model.
/// </summary>
public partial class ContactSheetView : UserControl
{
    public ContactSheetView()
    {
        InitializeComponent();
    }

    private ContactSheetViewModel? ViewModel => DataContext as ContactSheetViewModel;

    private static FigureCardViewModel? CardOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as FigureCardViewModel;

    private void Keep_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card) ViewModel?.ToggleKeep(card);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card) ViewModel?.RemoveCard(card);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card) ViewModel?.RequestEdit(card);
    }

    private void AddFigure_Click(object sender, RoutedEventArgs e) => ViewModel?.RequestAddFigure();
}
