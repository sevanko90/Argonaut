using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using Velopack;
using Velopack.Sources;
using Argonaut.Engine.Settings;

namespace Argonaut.Shell.Updates;

/// <summary>
/// Thin wrapper around Velopack's <see cref="UpdateManager"/>, sourcing releases straight from
/// GitHub Releases (see docs/velopack-auto-update-plan.md). All members are safe to call from
/// UI-originated async flows without explicit dispatching; the one exception is
/// <see cref="DownloadUpdatesAsync"/>'s progress callback, which Velopack may invoke from a
/// background thread, so it marshals via <c>Dispatcher.UIThread.Post</c> per the app's
/// threading convention.
/// </summary>
public sealed class UpdateService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly UpdateManager manager;
    private readonly UpdateSettings settings;

    public UpdateService(UpdateSettings settings)
    {
        this.settings = settings;
        manager = new UpdateManager(new GithubSource(AppInfo.RepoUrl, accessToken: null, prerelease: false));
    }

    /// <summary>
    /// False when running unpacked (e.g. `dotnet run`, or a plain portable zip with no
    /// Velopack-installed metadata) - there is nothing to check/apply updates against.
    /// </summary>
    public bool IsInstalled => manager.IsInstalled;

    public Task<UpdateInfo?> CheckForUpdatesAsync() => manager.CheckForUpdatesAsync();

    /// <summary>
    /// Gates the silent background startup check: off entirely when the user has disabled
    /// auto-update (see the About dialog), otherwise throttled to once per
    /// <see cref="CheckInterval"/> by the last check's time, remembered in <see cref="UpdateSettings"/>.
    /// Does not affect the manual "Check for Updates" toolbar action - disabling auto-update
    /// only stops the automatic check, not the user's ability to check on demand.
    /// </summary>
    public bool ShouldCheckOnStartup()
    {
        if (!settings.CheckOnStartup)
            return false;

        return settings.LastStartupCheckUtc is not { } last || DateTimeOffset.UtcNow - last >= CheckInterval;
    }

    public void RecordStartupCheck() =>
        settings.LastStartupCheckUtc = DateTimeOffset.UtcNow;

    public async Task DownloadUpdatesAsync(UpdateInfo updateInfo, Action<int> onProgress)
    {
        await manager.DownloadUpdatesAsync(updateInfo, progress => Dispatcher.UIThread.Post(() => onProgress(progress)));
    }

    public void ApplyUpdatesAndRestart(UpdateInfo updateInfo) => manager.ApplyUpdatesAndRestart(updateInfo);
}
