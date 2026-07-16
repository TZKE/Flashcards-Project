using System.Windows;

namespace AIFlashcardMaker;

/// <summary>
/// Phase 10: OPTIONAL update prompt, shown once per run when the backend reports a
/// newer version with forced=false. "Later" continues into the app with no penalty.
/// "Install Update" uses the existing secure updater workflow: the Velopack HTTPS
/// feed (download + apply + restart) when this is an installed copy, otherwise the
/// official installer download link in the browser. Updating never touches locally
/// stored research projects.
/// </summary>
public partial class UpdatePromptWindow : Window
{
    private readonly UpdatePolicyResult _policy;
    private bool _busy;

    public UpdatePromptWindow(UpdatePolicyResult policy)
    {
        InitializeComponent();
        _policy = policy;

        string latest = policy.LatestVersion ?? "a new version";
        VersionText.Text = $"You have v{AppConfig.CurrentVersionLabel} · latest v{latest} ({AppConfig.ReleaseChannel} channel)";
        MessageText.Text =
            $"OrbitLab {latest} is ready. This update improves project limits, progress tracking, and administration tools. " +
            "Your local research projects will remain on this device.";
        if (!string.IsNullOrWhiteSpace(policy.ReleaseNotes))
            ReleaseNotesText.Text = policy.ReleaseNotes;
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        DialogResult = false;   // normal use continues; the next launch may remind again
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        InstallButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        StatusText.Text = "Downloading the update… OrbitLab will restart automatically when it's ready.";
        try
        {
            // Existing secure path: Velopack verifies the package from the HTTPS feed,
            // applies it, and restarts the app (only works for an installed copy).
            var updater = UpdateServiceFactory.Create();
            var check = await updater.CheckAsync();
            if (check.Status == UpdateCheckStatus.UpdateAvailable && await updater.DownloadAndApplyAsync())
                return;   // ApplyUpdatesAndRestart exits the process on success

            // Fallback (dev/loose build or feed hiccup): the official installer link.
            if (InstallerSafety.IsAllowedUrl(_policy.InstallerUrl))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_policy.InstallerUrl!) { UseShellExecute = true });
                    StatusText.Text = "The download opened in your browser. Run the installer to update — your research projects stay on this device.";
                }
                catch { StatusText.Text = "Could not open the download link. Please use the official download link from the Telegram channel."; }
            }
            else
            {
                StatusText.Text = "The update could not be applied automatically. Please download OrbitLab again from the official link.";
            }
            _busy = false;
            InstallButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
        }
        catch
        {
            StatusText.Text = "The update could not be applied automatically. Please try again later.";
            _busy = false;
            InstallButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
        }
    }
}
