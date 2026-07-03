using System.Windows;
using System.Windows.Controls;

namespace AIFlashcardMaker;

/// <summary>
/// Reusable placeholder/watermark for TextBox. Set <c>local:Watermark.Text</c> on any
/// TextBox and the base TextBox template shows the placeholder while the box is empty.
///
/// Visibility is driven by <see cref="IsEmptyProperty"/>, which this helper keeps in
/// sync with the box on every TextChanged (and on the initial value). Because the flag
/// is updated in code the placeholder can never desync from the caret or "mix" with the
/// user's typed text — it disappears the instant the first character is typed and comes
/// back the moment the box is cleared.
/// </summary>
public static class Watermark
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached(
            "Text",
            typeof(string),
            typeof(Watermark),
            new PropertyMetadata(null, OnTextChanged));

    public static string GetText(DependencyObject obj) => (string)obj.GetValue(TextProperty);
    public static void SetText(DependencyObject obj, string value) => obj.SetValue(TextProperty, value);

    // True while the box is empty. Maintained in code; the template binds its
    // placeholder visibility to this flag.
    public static readonly DependencyProperty IsEmptyProperty =
        DependencyProperty.RegisterAttached(
            "IsEmpty",
            typeof(bool),
            typeof(Watermark),
            new PropertyMetadata(true));

    public static bool GetIsEmpty(DependencyObject obj) => (bool)obj.GetValue(IsEmptyProperty);
    public static void SetIsEmpty(DependencyObject obj, bool value) => obj.SetValue(IsEmptyProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
            return;

        // Detach first so re-setting the watermark never double-subscribes.
        box.TextChanged -= HandleTextChanged;

        if (e.NewValue is string watermark && !string.IsNullOrEmpty(watermark))
        {
            box.TextChanged += HandleTextChanged;
            SetIsEmpty(box, string.IsNullOrEmpty(box.Text));
        }
        else
        {
            SetIsEmpty(box, string.IsNullOrEmpty(box.Text));
        }
    }

    private static void HandleTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box)
            SetIsEmpty(box, string.IsNullOrEmpty(box.Text));
    }
}
