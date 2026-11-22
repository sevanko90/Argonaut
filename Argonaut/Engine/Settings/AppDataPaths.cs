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

#if DEBUG
    /// <summary>A Debug build's own settings, so a dev run never writes the released build's
    /// saved sources or preferences.</summary>
    public static string SettingsFile => Path.Combine(Root, "settings.debug.json");

    /// <summary>What a Debug build's settings start from on its first run.</summary>
    public static string? SettingsSeedFile => Path.Combine(Root, "settings.json");
#else
    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string? SettingsSeedFile => null;
#endif

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

    /// <summary>The struct definitions the hex view's Structures tab saves.</summary>
    public static string StructDefinitionsDirectory => Path.Combine(Root, "Structs");

    /// <summary>The diagnostic log a Debug build writes (see
    /// <see cref="Argonaut.Engine.Logging.FileDiagnosticLog"/>).</summary>
    /// <summary>Where downloaded responses are written and kept - the app's own folder, which the
    /// App Store sandbox always allows.</summary>
    public static string ResponsesDirectory => Path.Combine(Root, "Responses");

    /// <summary>Where a document with no file of its own (a response, a paste) is copied so
    /// another app can open it - cleared at each start (<see cref="Saving.OriginExporter.Sweep"/>).</summary>
    public static string OpenedDirectory => Path.Combine(Root, "Opened");

    /// <summary>Where macOS and Linux keep the key that seals settings blocks - see
    /// <see cref="FileSealingKeyStore"/>. Unused on Windows (DPAPI).</summary>
    public static string SealingKeyFile => Path.Combine(Root, "sealing.key");

    public static string DebugLogFile => Path.Combine(Root, "debug.log");
}
