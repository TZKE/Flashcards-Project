using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIFlashcardMaker.ChartsStudio.Application.Figures;
using AIFlashcardMaker.ChartsStudio.Presentation.ViewModels;

namespace AIFlashcardMaker.ChartsStudio.Presentation.Views;

/// <summary>
/// Charts Studio Phase 3 — Figure Editor view.
///
/// Forwards gestures to the view model; the only genuine code here is the swatch strip, which
/// is built in code because its buttons are data (palette hexes), not layout.
/// </summary>
public partial class FigureEditorView : UserControl
{
    public FigureEditorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookViewModel();
    }

    private FigureEditorViewModel? ViewModel => DataContext as FigureEditorViewModel;

    private void HookViewModel()
    {
        if (ViewModel is null) return;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(FigureEditorViewModel.ColorSwatches)
                or nameof(FigureEditorViewModel.IsOpen))
                RebuildSwatches();
        };
        RebuildSwatches();
    }

    /// <summary>Swatches come from the active palette, so they change with it.</summary>
    private void RebuildSwatches()
    {
        SwatchPanel.Children.Clear();
        if (ViewModel is null) return;

        foreach (string hex in ViewModel.ColorSwatches)
        {
            var button = new Button
            {
                Style = (Style)Resources["SwatchButton"],
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                ToolTip = hex
            };
            string captured = hex;
            button.Click += (_, _) => ViewModel?.PickSeriesColor(captured);
            SwatchPanel.Children.Add(button);
        }
    }

    // ---- Forwarders (no decisions here) ---------------------------------------------

    private void Save_Click(object sender, RoutedEventArgs e) => ViewModel?.Save();
    private void ResetAll_Click(object sender, RoutedEventArgs e) => ViewModel?.ResetAll();

    private void ResetText_Click(object sender, RoutedEventArgs e) => ViewModel?.ResetSection(EditSection.Text);
    private void ResetAppearance_Click(object sender, RoutedEventArgs e) => ViewModel?.ResetSection(EditSection.Appearance);
    private void ResetLayout_Click(object sender, RoutedEventArgs e) => ViewModel?.ResetSection(EditSection.Layout);

    private void ResetTitle_Click(object sender, RoutedEventArgs e) => ViewModel?.ResetField(EditField.Title);
    private void ResetSubtitle_Click(object sender, RoutedEventArgs e) => ViewModel?.ResetField(EditField.Subtitle);
    private void ResetCaption_Click(object sender, RoutedEventArgs e) => ViewModel?.ResetField(EditField.Caption);
    private void ResetXAxis_Click(object sender, RoutedEventArgs e) => ViewModel?.ResetField(EditField.XAxisTitle);
    private void ResetYAxis_Click(object sender, RoutedEventArgs e) => ViewModel?.ResetField(EditField.YAxisTitle);
    private void ResetSeriesColor_Click(object sender, RoutedEventArgs e) => ViewModel?.ResetField(EditField.SeriesColor);

    private void CancelDiscard_Click(object sender, RoutedEventArgs e) => ViewModel?.CancelDiscard();
    private void ConfirmDiscard_Click(object sender, RoutedEventArgs e) => ViewModel?.ConfirmDiscard();
    private void SaveAndClose_Click(object sender, RoutedEventArgs e) => ViewModel?.SaveAndClose();
}
