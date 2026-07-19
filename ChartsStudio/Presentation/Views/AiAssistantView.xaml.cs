using System.Windows;
using System.Windows.Controls;
using AIFlashcardMaker.ChartsStudio.Presentation.ViewModels;

namespace AIFlashcardMaker.ChartsStudio.Presentation.Views;

/// <summary>
/// Charts Studio Phase 6 — AI Advisory Assistant view. Forwards clicks to the view model, which
/// owns every decision. The async actions are fire-and-forget from the view's perspective; the
/// view model handles busy state and superseding.
/// </summary>
public partial class AiAssistantView : UserControl
{
    public AiAssistantView()
    {
        InitializeComponent();
    }

    private AiAssistantViewModel? ViewModel => DataContext as AiAssistantViewModel;

    private async void Caption_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.DraftCaptionAsync();
    }

    private async void Critique_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.CritiqueAsync();
    }

    private async void Accessibility_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.AccessibilityAsync();
    }

    private void ApplyCaption_Click(object sender, RoutedEventArgs e) => ViewModel?.ApplyCaption();

    private void Close_Click(object sender, RoutedEventArgs e) => ViewModel?.Close();
}
