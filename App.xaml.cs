using System.IO;
using System.Windows;
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
}
