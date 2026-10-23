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

    /// <summary>
    /// Folder holding the user's own recipes (see
    /// <see cref="Argonaut.Engine.Transforms.RecipeFolderStore"/>). Written to, unlike the schemas
    /// folder, which the user fills by hand - so a Mac App Store build must check this resolves
    /// inside its sandbox container (it is home-relative, and the sandbox moves home there).
    /// </summary>
    public static string RecipesDirectory => Path.Combine(Root, "Recipes");

    /// <summary>The diagnostic log a Debug build writes (see
    /// <see cref="Argonaut.Engine.Logging.FileDiagnosticLog"/>).</summary>
    public static string DebugLogFile => Path.Combine(Root, "debug.log");
}
