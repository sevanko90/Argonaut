using System;

namespace Argonaut.Infrastructure.Updates;

/// <summary>
/// Gates the silent background startup check: off entirely when the user has disabled
/// auto-update (see the About dialog), otherwise throttled to once per
/// <see cref="CheckInterval"/> via a marker file alongside the app's other settings files.
/// Does not affect the manual "Check for Updates" toolbar action - disabling auto-update
/// only stops the automatic check, not the user's ability to check on demand.
/// </summary>
public static class StartupUpdateCheck
{
    private const string MarkerFileName = "update-check.json";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    public static bool IsDue()
    {
        if (!AutoUpdatePreference.Load())
            return false;

        var marker = JsonSettingsStore.TryLoad<UpdateCheckMarker>(MarkerFileName);
        return marker is null || DateTimeOffset.UtcNow - marker.LastCheckUtc >= CheckInterval;
    }

    public static void Record() =>
        JsonSettingsStore.Save(MarkerFileName, new UpdateCheckMarker(DateTimeOffset.UtcNow));

    private sealed record UpdateCheckMarker(DateTimeOffset LastCheckUtc);
}
