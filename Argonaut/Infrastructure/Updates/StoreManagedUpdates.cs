using System;
using System.Threading.Tasks;

namespace Argonaut.Infrastructure.Updates;

/// <summary>
/// The updater for store builds: the store delivers updates, and store policy forbids an app
/// updating itself, so this build offers no update affordances and never finds a release to act on.
/// Compiled into every channel (only <see cref="AppUpdaters"/> chooses it), so the GitHub build
/// keeps it compiling.
/// </summary>
public sealed class StoreManagedUpdates : IAppUpdater
{
    public bool SupportsSelfUpdate => false;

    public bool IsInstalled => false;

    public void OnProcessStart()
    {
    }

    public Task<AvailableUpdate?> CheckForUpdatesAsync() => Task.FromResult<AvailableUpdate?>(null);

    public Task DownloadUpdatesAsync(AvailableUpdate update, Action<int> onProgress) =>
        throw new NotSupportedException("Store builds are updated by the store.");

    public void ApplyUpdatesAndRestart(AvailableUpdate update) =>
        throw new NotSupportedException("Store builds are updated by the store.");
}
