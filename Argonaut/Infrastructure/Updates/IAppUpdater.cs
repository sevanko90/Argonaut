using System;
using System.Threading.Tasks;

namespace Argonaut.Infrastructure.Updates;

/// <summary>
/// How this build keeps itself current. Which implementation a build carries is decided at compile
/// time by the <c>DistributionChannel</c> MSBuild property (see Argonaut.csproj and
/// <see cref="AppUpdaters"/>): the GitHub channel updates itself through Velopack, while a store
/// channel leaves updating to the store and ships no self-update code at all.
/// </summary>
public interface IAppUpdater
{
    /// <summary>
    /// Whether this build's distribution channel updates itself. False hides every update
    /// affordance (the toolbar button, the About dialog's checkbox) rather than showing controls
    /// that can never act.
    /// </summary>
    bool SupportsSelfUpdate { get; }

    /// <summary>
    /// False when running unpacked (e.g. `dotnet run`, or a plain portable zip with no installer
    /// metadata) - there is nothing to check/apply updates against.
    /// </summary>
    bool IsInstalled { get; }

    /// <summary>
    /// Called first thing in Main, before anything touches Avalonia, for an updater that must
    /// intercept a launch made by its own install/update lifecycle.
    /// </summary>
    void OnProcessStart();

    /// <summary>The newer release available, or null when this build is up to date.</summary>
    Task<AvailableUpdate?> CheckForUpdatesAsync();

    /// <summary>Downloads <paramref name="update"/>; <paramref name="onProgress"/> is invoked on the UI thread.</summary>
    Task DownloadUpdatesAsync(AvailableUpdate update, Action<int> onProgress);

    void ApplyUpdatesAndRestart(AvailableUpdate update);
}

/// <summary>
/// A newer release an <see cref="IAppUpdater"/> found. Each updater derives its own record to carry
/// whatever handle it needs to download and apply the release, so no caller names that type.
/// </summary>
public abstract record AvailableUpdate(string Version);
