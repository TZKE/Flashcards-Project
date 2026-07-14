using Velopack;

namespace AIFlashcardMaker;

/// <summary>
/// Phase 6A installer/updater foundation. Velopack provides install/update
/// plumbing; this abstraction keeps the rest of the app (including the
/// forced-update UI later) independent of it.
///
/// IMPORTANT: no public update feed exists yet (no domain/HTTPS). Until
/// ORBITLAB_UPDATE_FEED or "updateFeedUrl" in orbitlab.settings.json is
/// configured — https only, http for localhost testing — this service reports
/// NotConfigured and does nothing. It never downloads or applies anything on
/// its own; callers drive it from explicit user actions.
/// </summary>
public enum UpdateCheckStatus
{
    NotConfigured,      // no feed URL configured (the normal state until domain+HTTPS)
    NotInstalled,       // running a dev/loose build, not a Velopack-installed app
    UpToDate,
    UpdateAvailable,
    Failed,
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, string? AvailableVersion = null);

public interface IUpdateService
{
    bool IsConfigured { get; }
    Task<UpdateCheckResult> CheckAsync();
    /// <summary>Downloads and applies a previously found update, then restarts. False when not possible.</summary>
    Task<bool> DownloadAndApplyAsync();
}

public static class UpdateServiceFactory
{
    public static IUpdateService Create() =>
        string.IsNullOrEmpty(AppConfig.UpdateFeedUrl)
            ? new DisabledUpdateService()
            : new VelopackUpdateService(AppConfig.UpdateFeedUrl);
}

/// <summary>Inert placeholder used until an update feed is configured.</summary>
public sealed class DisabledUpdateService : IUpdateService
{
    public bool IsConfigured => false;
    public Task<UpdateCheckResult> CheckAsync() =>
        Task.FromResult(new UpdateCheckResult(UpdateCheckStatus.NotConfigured));
    public Task<bool> DownloadAndApplyAsync() => Task.FromResult(false);
}

public sealed class VelopackUpdateService : IUpdateService
{
    private readonly string _feedUrl;
    private UpdateManager? _manager;
    private UpdateInfo? _pending;

    public VelopackUpdateService(string feedUrl) => _feedUrl = feedUrl;

    public bool IsConfigured => true;

    public async Task<UpdateCheckResult> CheckAsync()
    {
        try
        {
            _manager ??= new UpdateManager(_feedUrl);
            if (!_manager.IsInstalled)
                return new UpdateCheckResult(UpdateCheckStatus.NotInstalled);

            _pending = await _manager.CheckForUpdatesAsync();
            return _pending is null
                ? new UpdateCheckResult(UpdateCheckStatus.UpToDate)
                : new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, _pending.TargetFullRelease.Version.ToString());
        }
        catch
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed);
        }
    }

    public async Task<bool> DownloadAndApplyAsync()
    {
        try
        {
            if (_manager is null || !_manager.IsInstalled || _pending is null) return false;
            await _manager.DownloadUpdatesAsync(_pending);
            _manager.ApplyUpdatesAndRestart(_pending);   // exits the process
            return true;
        }
        catch
        {
            return false;
        }
    }
}
