using System;
using System.IO;

namespace Argonaut.Engine.Settings;

/// <summary>
/// Where the app keeps its own files in the user's profile. Read by the composition root, which
/// hands each path to what uses it - nothing below the root asks this class directly.
/// </summary>
internal static class AppDataPaths
{
    private static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Argonaut");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>
    /// Folder where the user drops their own JSON Schema files (see
    /// <c>Argonaut.Features.Json.Schema.JsonSchemaCatalog</c>).
    /// </summary>
    public static string SchemasDirectory => Path.Combine(Root, "Schemas");

    public static string GetFilePath(string fileName) => Path.Combine(Root, fileName);
}
