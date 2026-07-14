using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace AIFlashcardMaker;

/// <summary>
/// Phase 6A: client for the backend's PUBLIC, read-only metadata endpoints
/// (GET /api/public/bootstrap and GET /api/app/version). These calls send NOTHING
/// except the query string — no account data and never any research data — and
/// every failure path degrades silently: no backend, unreachable backend, or bad
/// JSON simply mean "no update information available".
/// </summary>
public sealed class BootstrapInfo
{
    public string? ProductName { get; set; }
    public string? CompanyName { get; set; }
    public string? TelegramChannelUrl { get; set; }
    public string? SupportEmail { get; set; }
    public string? PublicBetaNotice { get; set; }
    public string? TrustedSellersPolicyText { get; set; }
}

public sealed class UpdatePolicyResult
{
    public string? ProductName { get; set; }
    public string? Channel { get; set; }
    public string? LatestVersion { get; set; }
    public string? MinimumSupportedVersion { get; set; }
    public bool Forced { get; set; }
    public string? InstallerUrl { get; set; }
    public string? ReleaseNotes { get; set; }
    public string? Sha256 { get; set; }
    public string? TelegramChannelUrl { get; set; }
    public string? SupportEmail { get; set; }
    public string? Message { get; set; }
}

public static class UpdatePolicyClient
{
    // Startup checks must never make the app feel slow: hard 5-second budget.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AIFlashcardMaker", "bootstrap.cache.json");

    /// <summary>Fetches public bootstrap info and refreshes the local cache. Null on any failure.</summary>
    public static async Task<BootstrapInfo?> TryGetBootstrapAsync()
    {
        if (!AppConfig.IsBackendConfigured) return null;
        try
        {
            var json = await Http.GetStringAsync($"{AppConfig.BackendBaseUrl}/api/public/bootstrap");
            var info = JsonSerializer.Deserialize<BootstrapInfo>(json, JsonOpts);
            if (info is not null)
            {
                try { File.WriteAllText(CachePath, json); } catch { /* cache is best-effort */ }
            }
            return info;
        }
        catch { return null; }
    }

    /// <summary>Last successfully fetched bootstrap info (survives offline launches). Null if none.</summary>
    public static BootstrapInfo? LoadCachedBootstrap()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            return JsonSerializer.Deserialize<BootstrapInfo>(File.ReadAllText(CachePath), JsonOpts);
        }
        catch { return null; }
    }

    /// <summary>Fetches the update policy for this build's channel. Null on any failure.</summary>
    public static async Task<UpdatePolicyResult?> TryGetVersionPolicyAsync()
    {
        if (!AppConfig.IsBackendConfigured) return null;
        try
        {
            var json = await Http.GetStringAsync(
                $"{AppConfig.BackendBaseUrl}/api/app/version?channel={AppConfig.ReleaseChannel}&platform=windows");
            return JsonSerializer.Deserialize<UpdatePolicyResult>(json, JsonOpts);
        }
        catch { return null; }
    }
}

public static class VersionUtil
{
    /// <summary>
    /// Parses "0.9.1", "v0.9.1" or "0.9.1-beta" into a Version. Null when unparseable.
    /// </summary>
    public static Version? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().TrimStart('v', 'V');
        int dash = s.IndexOf('-');
        if (dash > 0) s = s[..dash];
        return Version.TryParse(s, out var v) ? Normalize(v) : null;
    }

    /// <summary>True when <paramref name="current"/> is strictly below <paramref name="other"/>.</summary>
    public static bool IsBelow(Version current, string? other)
    {
        var o = TryParse(other);
        return o is not null && Normalize(current) < o;
    }

    // "1.2" and "1.2.0.0" must compare equal: pad missing parts with zeros.
    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}

/// <summary>
/// Safety rules for installer downloads. Production downloads must be HTTPS;
/// http is tolerated only for loopback (local staging tests). Nothing is ever
/// executed silently — the user drives every step, and when the backend
/// publishes a SHA256 the downloaded file must match it.
/// </summary>
public static class InstallerSafety
{
    public static bool IsAllowedUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        (u.Scheme == Uri.UriSchemeHttps ||
         (u.Scheme == Uri.UriSchemeHttp && (u.IsLoopback || u.Host is "localhost" or "127.0.0.1")));

    public static bool IsHttpsUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    public static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static bool Sha256Matches(string filePath, string? expectedHex) =>
        !string.IsNullOrWhiteSpace(expectedHex) &&
        string.Equals(ComputeSha256(filePath), expectedHex.Trim().ToLowerInvariant(), StringComparison.Ordinal);
}
