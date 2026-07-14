using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIFlashcardMaker;

/// <summary>
/// Phase 7 (Checkpoint D): locally cached license session.
///
/// Stored encrypted with Windows DPAPI (per-user scope) at
/// %APPDATA%\AIFlashcardMaker\license.bin — never in plaintext, never in git.
/// It contains ONLY: the staging session token, account email/name, subscription
/// status + entitlements, device binding info, and heartbeat timestamps.
/// It never stores the raw password or the activation code.
///
/// HONESTY NOTE: the cached token is the backend's staging session token
/// (Phase 5A). It is NOT a signed offline license. Offline grace below is a
/// client-side policy on top of the last successful heartbeat; Phase 5B's
/// Ed25519 signed tokens will replace <see cref="LicenseStore"/> validation
/// without changing callers.
/// </summary>
public sealed class LicenseCache
{
    public int UserId { get; set; }
    public string Email { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Token { get; set; } = "";          // staging session token (opaque)
    public int SubscriptionId { get; set; }
    public string PlanCode { get; set; } = "";
    public string PlanName { get; set; } = "";
    public string Status { get; set; } = "";         // Active / Suspended / Revoked / Expired
    public string EntitlementsJson { get; set; } = "";
    public int GraceDays { get; set; } = 14;
    public DateTime? EndsAtUtc { get; set; }
    public DateTime LastHeartbeatUtc { get; set; }
    public int DeviceActivationId { get; set; }
    public string DeviceHash { get; set; } = "";

    public bool IsActive => string.Equals(Status, "Active", StringComparison.OrdinalIgnoreCase);

    /// <summary>Within the offline-grace window measured from the last successful heartbeat.</summary>
    public bool WithinOfflineGrace =>
        DateTime.UtcNow - LastHeartbeatUtc <= TimeSpan.FromDays(Math.Clamp(GraceDays, 0, 90));

    /// <summary>License considered usable for protected actions right now.</summary>
    public bool AllowsProtectedActions => IsActive && WithinOfflineGrace;

    public int MaxProjects => ReadIntEntitlement("maxProjects", 1);

    private int ReadIntEntitlement(string key, int fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(EntitlementsJson) ? "{}" : EntitlementsJson);
            return doc.RootElement.TryGetProperty(key, out var v) && v.TryGetInt32(out int n) ? n : fallback;
        }
        catch { return fallback; }
    }
}

public static class LicenseStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AIFlashcardMaker", "license.bin");

    // Not a secret: DPAPI entropy just namespaces the blob to this app.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OrbitLab.LicenseCache.v1");

    public static void Save(LicenseCache cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(cache);
            byte[] protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, protectedBytes);
        }
        catch { /* cache is an optimization; failing to persist must never crash the app */ }
    }

    public static LicenseCache? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<LicenseCache>(plain);
        }
        catch { return null; }   // corrupt/foreign blob → treated as signed-out
    }

    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { /* best effort */ }
    }
}

public static class DeviceIdentity
{
    /// <summary>
    /// Stable, privacy-preserving device hash: SHA-256 over an app namespace,
    /// the Windows installation's MachineGuid (a random GUID created by Windows
    /// setup — not a hardware serial), and the local username. No raw hardware
    /// identifiers, MAC addresses, or serials are read or transmitted.
    /// </summary>
    public static string ComputeDeviceHash()
    {
        string machineGuid = "";
        try
        {
            machineGuid = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography")?
                .GetValue("MachineGuid")?.ToString() ?? "";
        }
        catch { /* fall back to name-based identity below */ }
        if (string.IsNullOrEmpty(machineGuid))
            machineGuid = Environment.MachineName;

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"OrbitLab.device.v1|{machineGuid}|{Environment.UserName}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Short human label for the admin Devices page (no serials).</summary>
    public static string DeviceName => Environment.MachineName;

    public static string OsInfo => $"Windows {Environment.OSVersion.Version}";
}
