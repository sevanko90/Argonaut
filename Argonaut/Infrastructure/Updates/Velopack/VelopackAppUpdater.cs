using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using Velopack;
using Velopack.Sources;

// Deliberately not Argonaut.Infrastructure.Updates.Velopack: a namespace segment named Velopack
// would shadow the Velopack namespace imported above.
namespace Argonaut.Infrastructure.Updates;

/// <summary>
/// The GitHub channel's updater: Velopack's <see cref="UpdateManager"/>, sourcing releases straight
/// from GitHub Releases (see docs/velopack-auto-update-plan.md). Compiled only when
/// DistributionChannel is GitHub - Argonaut.csproj removes this folder from every other channel.
/// All members are safe to call from UI-originated async flows without explicit dispatching; the
/// one exception is <see cref="DownloadUpdatesAsync"/>'s progress callback, which Velopack may
/// invoke from a background thread, so it marshals via <c>Dispatcher.UIThread.Post</c> per the
/// app's threading convention.
/// </summary>
public sealed class VelopackAppUpdater : IAppUpdater
{
    // Built on first use, not at construction: this updater is created in Main before Avalonia
    // starts, and OnProcessStart needs no manager.
    private readonly Lazy<UpdateManager> manager = new(() =>
        new UpdateManager(new GithubSource(AppInfo.RepoUrl, accessToken: null, prerelease: false)));

    public bool SupportsSelfUpdate => true;

    public bool IsInstalled => manager.Value.IsInstalled;

    /// <summary>
    /// Handles a pending install/update completion (e.g. Windows relaunching post-update) and then
    /// returns normally on a regular launch.
    /// AutoApplyOnStartup is ON by default, meaning every launch silently swaps in
    /// whatever's the highest-versioned .nupkg sitting in Velopack's local package cache
    /// (~/Library/Caches/velopack/&lt;app&gt;/packages on macOS) - not just updates staged by
    /// this updater. That cache accumulates across every local packaging run, so
    /// a local dev build with a lower version than a previously packed one gets silently
    /// replaced on launch with no dialog. <see cref="ApplyUpdatesAndRestart"/> still applies
    /// updates explicitly (after user confirmation) regardless of this setting - only the
    /// implicit on-startup swap is disabled.
    /// </summary>
    public void OnProcessStart() => VelopackApp.Build().SetAutoApplyOnStartup(false).Run();

    public async Task<AvailableUpdate?> CheckForUpdatesAsync()
    {
        UpdateInfo? release = await manager.Value.CheckForUpdatesAsync();
        return release is null ? null : new VelopackUpdate(release);
    }

    public async Task DownloadUpdatesAsync(AvailableUpdate update, Action<int> onProgress)
    {
        await manager.Value.DownloadUpdatesAsync(
            ReleaseOf(update), progress => Dispatcher.UIThread.Post(() => onProgress(progress)));
    }

    public void ApplyUpdatesAndRestart(AvailableUpdate update) =>
        manager.Value.ApplyUpdatesAndRestart(ReleaseOf(update));

    private static UpdateInfo ReleaseOf(AvailableUpdate update) => ((VelopackUpdate)update).Release;

    private sealed record VelopackUpdate(UpdateInfo Release)
        : AvailableUpdate(Release.TargetFullRelease.Version.ToString());
}
