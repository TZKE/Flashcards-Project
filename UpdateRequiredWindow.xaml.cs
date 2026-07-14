using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;

namespace AIFlashcardMaker;

/// <summary>
/// Phase 6A forced-update gate. Blocking by design: closing it (button or X)
/// shuts the app down. Safety rules:
///  - downloads only from HTTPS URLs (http allowed for loopback staging tests),
///  - nothing runs silently — the user clicks "Download and Update" and then
///    explicitly "Open installer",
///  - when the backend publishes a SHA256 the file must match or it is deleted,
///  - without a SHA256 the installer is never launched from here; the user gets
///    the folder and a clear warning instead.
/// </summary>
public partial class UpdateRequiredWindow : Window
{
    private readonly UpdatePolicyResult _policy;
    private readonly string? _telegramUrl;
    private bool _closingHandled;
    private string? _downloadedFile;

    public UpdateRequiredWindow(UpdatePolicyResult policy, string? telegramUrl)
    {
        InitializeComponent();
        _policy = policy;
        _telegramUrl = telegramUrl;

        Title = $"{Branding.ProductName} — required update";
        VersionText.Text =
            $"You have v{AppConfig.CurrentVersionDisplay} · minimum supported v{policy.MinimumSupportedVersion} · latest v{policy.LatestVersion} ({AppConfig.ReleaseChannel} channel)";
        ReleaseNotesText.Text = string.IsNullOrWhiteSpace(policy.ReleaseNotes)
            ? "No release notes were provided for this update."
            : policy.ReleaseNotes;
        SupportText.Text = string.IsNullOrWhiteSpace(policy.SupportEmail)
            ? "Update downloads and instructions are announced in the official Telegram channel."
            : $"Need help? Contact {policy.SupportEmail} or check the official Telegram channel.";

        if (string.IsNullOrWhiteSpace(policy.InstallerUrl))
        {
            DownloadButton.IsEnabled = false;
            StatusText.Text = "The download link is not published yet. Please get the update from the official Telegram channel.";
        }
        else if (!InstallerSafety.IsAllowedUrl(policy.InstallerUrl))
        {
            DownloadButton.IsEnabled = false;
            StatusText.Text = "The published download link is not a secure (HTTPS) address, so it was blocked for your safety. Please get the update from the official Telegram channel.";
        }
        else if (!InstallerSafety.IsHttpsUrl(policy.InstallerUrl))
        {
            StatusText.Text = "Staging/local update link (not HTTPS) — allowed for local testing only.";
        }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (!InstallerSafety.IsAllowedUrl(_policy.InstallerUrl)) return;

        DownloadButton.IsEnabled = false;
        StatusText.Text = "Downloading the update… this can take a few minutes.";
        try
        {
            var uri = new Uri(_policy.InstallerUrl!);
            string name = Path.GetFileName(uri.LocalPath);
            if (string.IsNullOrWhiteSpace(name)) name = "orbitlab-update-download";
            string dir = Path.Combine(Path.GetTempPath(), "OrbitLabUpdate");
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, name);

            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            await using (var download = await http.GetStreamAsync(uri))
            await using (var file = File.Create(target))
                await download.CopyToAsync(file);

            if (!string.IsNullOrWhiteSpace(_policy.Sha256))
            {
                if (InstallerSafety.Sha256Matches(target, _policy.Sha256))
                {
                    _downloadedFile = target;
                    StatusText.Text = "Download complete and checksum verified. Click \"Open installer\" to run it — OrbitLab will close so the update can install.";
                    OpenInstallerButton.Visibility = Visibility.Visible;
                }
                else
                {
                    try { File.Delete(target); } catch { /* best effort */ }
                    StatusText.Text = "SECURITY WARNING: the downloaded file did not match the published checksum and was deleted. Do not install it. Please report this in the official Telegram channel or to support.";
                    DownloadButton.IsEnabled = true;
                }
            }
            else
            {
                // No published checksum: never launch from here. Hand the user the
                // folder with a clear warning instead.
                _downloadedFile = target;
                StatusText.Text = "Download complete, but no checksum was published for this file, so OrbitLab will not run it automatically. Open the folder and run the installer yourself only if you trust the source (official Telegram channel announcement).";
                OpenFolderButton.Visibility = Visibility.Visible;
            }
        }
        catch
        {
            StatusText.Text = "The download failed. Check your internet connection and try again, or get the update from the official Telegram channel.";
            DownloadButton.IsEnabled = true;
        }
    }

    private void OpenInstaller_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadedFile is null || !File.Exists(_downloadedFile)) return;
        try
        {
            Process.Start(new ProcessStartInfo(_downloadedFile) { UseShellExecute = true });
            CloseApp_Click(sender, e);   // quit so the installer can replace files
        }
        catch
        {
            StatusText.Text = "Could not start the installer. Open the download folder and run it manually.";
            OpenFolderButton.Visibility = Visibility.Visible;
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadedFile is null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_downloadedFile}\"") { UseShellExecute = true }); }
        catch { /* non-fatal */ }
    }

    private void JoinTelegram_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_telegramUrl))
        {
            StatusText.Text = "The official Telegram channel link is not configured yet.";
            return;
        }
        try { Process.Start(new ProcessStartInfo(_telegramUrl) { UseShellExecute = true }); }
        catch { StatusText.Text = "Could not open the Telegram channel link."; }
    }

    private void CloseApp_Click(object sender, RoutedEventArgs e)
    {
        _closingHandled = true;
        Application.Current.Shutdown();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // The gate must not be escapable via the X button.
        if (!_closingHandled) Application.Current.Shutdown();
    }
}
