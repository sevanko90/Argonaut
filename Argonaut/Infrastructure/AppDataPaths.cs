using System;
using System.IO;

namespace Argonaut.Infrastructure;

internal static class AppDataPaths
{
    /// <summary>
    /// Test seam: when set, settings files resolve under this directory instead of the real
    /// user profile, so tests never read or clobber the developer's own Argonaut settings.
    /// Null in production. See <c>Argonaut.Tests</c> (exposed via InternalsVisibleTo).
    /// </summary>
    internal static string? RootOverride;

    private static string Root =>
        RootOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Argonaut");

    public static string GetSettingsFilePath(string fileName) => Path.Combine(Root, fileName);
}
