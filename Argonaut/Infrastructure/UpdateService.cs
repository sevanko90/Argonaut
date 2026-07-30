using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using Velopack;
using Velopack.Sources;

namespace Argonaut.Infrastructure;

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
    private const string RepoUrl = "https://github.com/sevanko90/Argonaut";
    private const string MarkerFileName = "update-check.json";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly UpdateManager manager;

    public UpdateService()
    {
        manager = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
    }

    /// <summary>
    /// False when running unpacked (e.g. `dotnet run`, or a plain portable zip with no
    /// Velopack-installed metadata) - there is nothing to check/apply updates against.
    /// </summary>
    public bool IsInstalled => manager.IsInstalled;

    public Task<UpdateInfo?> CheckForUpdatesAsync() => manager.CheckForUpdatesAsync();

    /// <summary>
    /// Throttles the silent background startup check to once per <see cref="CheckInterval"/>,
    /// tracked via a marker file alongside the app's other settings files. Best-effort: any
    /// read failure just allows the check to proceed.
    /// </summary>
    public bool ShouldCheckOnStartup()
    {
        var marker = JsonSettingsStore.TryLoad<UpdateCheckMarker>(MarkerFileName);
        return marker is null || DateTimeOffset.UtcNow - marker.LastCheckUtc >= CheckInterval;
    }

    public void RecordStartupCheck() =>
        JsonSettingsStore.Save(MarkerFileName, new UpdateCheckMarker(DateTimeOffset.UtcNow));

    public async Task DownloadUpdatesAsync(UpdateInfo updateInfo, Action<int> onProgress)
    {
        await manager.DownloadUpdatesAsync(updateInfo, progress => Dispatcher.UIThread.Post(() => onProgress(progress)));
    }

    public void ApplyUpdatesAndRestart(UpdateInfo updateInfo) => manager.ApplyUpdatesAndRestart(updateInfo);

    private sealed record UpdateCheckMarker(DateTimeOffset LastCheckUtc);
}
