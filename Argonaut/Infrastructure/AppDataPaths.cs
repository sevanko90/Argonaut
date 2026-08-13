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

    /// <summary>
    /// Folder where the user drops their own JSON Schema files (see
    /// <c>Argonaut.Features.Json.Schema.JsonSchemaCatalog</c>). Not created here - enumeration
    /// tolerates it being absent, and only the toolbar's "open schema folder" action creates it.
    /// </summary>
    public static string GetSchemasDirectory() => Path.Combine(Root, "Schemas");
}
