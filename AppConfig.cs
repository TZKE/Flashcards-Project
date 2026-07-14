using System.IO;
using System.Reflection;
using System.Text.Json;

namespace AIFlashcardMaker;

/// <summary>
/// Phase 6A: central app configuration for the OrbitLab Commercial Beta.
///
/// IMPORTANT: there is deliberately NO hardcoded production backend/API/update URL
/// here — no domain or HTTPS exists yet. The backend base URL is optional and comes
/// from (in order): the ORBITLAB_BACKEND_URL environment variable, an
/// <c>orbitlab.settings.json</c> file next to the exe, or the same file in the
/// app's AppData folder. When it is missing or invalid the app runs fully offline
/// exactly as before — nothing crashes, nothing is blocked.
///
/// The settings file contains NO secrets, only e.g.:
///   { "backendBaseUrl": "http://127.0.0.1:5000", "updateFeedUrl": "" }
/// (http is accepted for localhost/127.0.0.1 staging only; anything else must be https).
/// </summary>
public static class AppConfig
{
    public const string ProductName = Branding.ProductName;
    public const string CompanyName = Branding.CompanyName;

    /// <summary>Update channel this build follows. Commercial Beta ships on "beta".</summary>
    public const string ReleaseChannel = "beta";

    public const string SettingsFileName = "orbitlab.settings.json";

    private static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIFlashcardMaker");

    /// <summary>Current app version from the assembly (csproj &lt;Version&gt;).</summary>
    public static Version CurrentVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    /// <summary>Three-part display form, e.g. "7.4.0".</summary>
    public static string CurrentVersionDisplay { get; } =
        $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{Math.Max(CurrentVersion.Build, 0)}";

    private static bool _loaded;
    private static string? _backendBaseUrl;
    private static string? _updateFeedUrl;

    /// <summary>Backend base URL (no trailing slash) or null when not configured.</summary>
    public static string? BackendBaseUrl { get { EnsureLoaded(); return _backendBaseUrl; } }

    /// <summary>Velopack update feed URL or null when not configured (the normal state until domain+HTTPS).</summary>
    public static string? UpdateFeedUrl { get { EnsureLoaded(); return _updateFeedUrl; } }

    public static bool IsBackendConfigured => !string.IsNullOrEmpty(BackendBaseUrl);

    private sealed class SettingsFile
    {
        public string? BackendBaseUrl { get; set; }
        public string? UpdateFeedUrl { get; set; }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            _backendBaseUrl = NormalizeServiceUrl(Environment.GetEnvironmentVariable("ORBITLAB_BACKEND_URL"));
            _updateFeedUrl = NormalizeServiceUrl(Environment.GetEnvironmentVariable("ORBITLAB_UPDATE_FEED"));

            foreach (var dir in new[] { AppContext.BaseDirectory, AppDataDir })
            {
                if (_backendBaseUrl is not null && _updateFeedUrl is not null) break;
                var file = ReadSettingsFile(Path.Combine(dir, SettingsFileName));
                if (file is null) continue;
                _backendBaseUrl ??= NormalizeServiceUrl(file.BackendBaseUrl);
                _updateFeedUrl ??= NormalizeServiceUrl(file.UpdateFeedUrl);
            }
        }
        catch
        {
            // Config loading must never break app startup; run as not-configured.
            _backendBaseUrl = null;
            _updateFeedUrl = null;
        }
    }

    private static SettingsFile? ReadSettingsFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }   // malformed local config is ignored, never fatal
    }

    /// <summary>
    /// Accepts an absolute https URL, or http for loopback/localhost (staging/dev only).
    /// Anything else — including a bare public IP over http — is rejected so a
    /// plaintext production endpoint can never be configured by accident.
    /// </summary>
    internal static string? NormalizeServiceUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (raw.StartsWith("{{", StringComparison.Ordinal)) return null;   // placeholder token
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return null;
        bool ok = uri.Scheme == Uri.UriSchemeHttps ||
                  (uri.Scheme == Uri.UriSchemeHttp && (uri.IsLoopback || uri.Host is "localhost" or "127.0.0.1"));
        return ok ? raw.TrimEnd('/') : null;
    }
}
