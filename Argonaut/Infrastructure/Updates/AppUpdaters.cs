namespace Argonaut.Infrastructure.Updates;

/// <summary>
/// The one place a build's distribution channel picks its updater. <c>SELF_UPDATE</c> is defined by
/// Argonaut.csproj for the GitHub channel only; every other channel builds without the Velopack
/// package and without the Updates/Velopack folder, so nothing outside that folder may name a
/// Velopack type.
/// </summary>
public static class AppUpdaters
{
    public static IAppUpdater Current { get; } =
#if SELF_UPDATE
        new VelopackAppUpdater();
#else
        new StoreManagedUpdates();
#endif
}
