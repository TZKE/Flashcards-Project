using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AIFlashcardMaker;

public partial class App : Application
{
    public App()
    {
        // Phase 6A: Velopack updater hooks must run as early as possible. For a
        // normal (non-Velopack-installed) build this is a documented safe no-op —
        // startup behavior is unchanged. Wrapped defensively anyway: the updater
        // must never be able to stop OrbitLab from opening.
        try { Velopack.VelopackApp.Build().Run(); }
        catch { /* updater bootstrap is best-effort */ }

        // Global safety net: a bug in one action (e.g. a Data Extraction button)
        // must never hard-close the whole app. Log the details for diagnosis and
        // keep the window open. No sensitive data (keys) is ever written here.
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Keep the page scrolling when the pointer is over an inner scrollable
        // control (see RegisterScrollBehaviour). Deliberately AFTER the Velopack
        // bootstrap above, which must stay first, and before any window exists
        // so the handlers apply everywhere.
        RegisterScrollBehaviour();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIFlashcardMaker");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"), $"[{DateTime.Now:u}] {e.Exception}\n\n");
        }
        catch { /* logging must never itself crash the handler */ }

        try
        {
            MessageBox.Show(
                "Something went wrong with that action, but your work is safe and the app is still open. Please try again.",
                Branding.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { /* ignore */ }

        e.Handled = true;   // keep the application alive
    }

    // =======================================================================
    // Scrolling behaviour
    //
    // WPF marks MouseWheel as handled inside any control that owns a
    // ScrollViewer — every multiline TextBox, every DataGrid, every nested
    // ScrollViewer. OrbitLab stacks 55+ full-width multiline editors down its
    // pages, so a user scrolling with the pointer near the middle of a page
    // lands on one, and the page silently stops. Because it depends on where
    // the pointer happens to be, it reads as broken rather than slow — the
    // main cause of "scrolling is laggy and not easy to handle".
    //
    // Fix: when the inner control has no room left in the wheel's direction,
    // re-raise the event to its parent so the page keeps moving. Nothing is
    // taken away — an inner control that CAN still scroll is untouched.
    // =======================================================================
    private static void RegisterScrollBehaviour()
    {
        var handler = new MouseWheelEventHandler(OnPreviewMouseWheelBubbleAtLimit);
        EventManager.RegisterClassHandler(typeof(TextBoxBase),  UIElement.PreviewMouseWheelEvent, handler);
        EventManager.RegisterClassHandler(typeof(DataGrid),     UIElement.PreviewMouseWheelEvent, handler);
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent, handler);
    }

    private static void OnPreviewMouseWheelBubbleAtLimit(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0) return;
        if (sender is not UIElement element) return;

        // PreviewMouseWheel tunnels outside-in, so an outer ScrollViewer sees
        // the event before the control actually under the pointer does. Only
        // the innermost scrollable region may act on it.
        if (!IsNearestScrollableTo(element, e.OriginalSource)) return;

        ScrollViewer? viewer = element as ScrollViewer ?? FindDescendantScrollViewer(element);
        if (viewer is null) return;

        const double epsilon = 0.5;
        bool hasRoom = e.Delta > 0
            ? viewer.VerticalOffset > epsilon
            : viewer.VerticalOffset < viewer.ScrollableHeight - epsilon;
        if (hasRoom) return;   // inner content can still move: leave it alone

        e.Handled = true;
        if (VisualTreeHelper.GetParent(element) is UIElement parent)
        {
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = element,
            });
        }
    }

    // True when no other scrollable control sits between the pointer target
    // and this element.
    private static bool IsNearestScrollableTo(DependencyObject element, object originalSource)
    {
        if (originalSource is not DependencyObject cursor) return false;
        while (cursor is not null && cursor != element)
        {
            if (cursor is ScrollViewer || cursor is TextBoxBase || cursor is DataGrid) return false;
            cursor = cursor is Visual || cursor is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(cursor) ?? LogicalTreeHelper.GetParent(cursor)
                : LogicalTreeHelper.GetParent(cursor);
        }
        return cursor == element;
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            ScrollViewer? nested = FindDescendantScrollViewer(child);
            if (nested is not null) return nested;
        }
        return null;
    }
}
