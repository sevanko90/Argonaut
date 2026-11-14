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
    /// <summary>A Debug build's own settings, so it never writes the released build's. Its sealed
    /// blocks use a different key (see <c>SettingsSealers</c>), and sharing one file would have
    /// each build overwrite the other's saved sources with a blob the other cannot open.</summary>
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

    /// <summary>The diagnostic log a Debug build writes (see
    /// <see cref="Argonaut.Engine.Logging.FileDiagnosticLog"/>).</summary>
    /// <summary>Where downloaded responses are written and kept - the app's own folder, which the
    /// App Store sandbox always allows.</summary>
    public static string ResponsesDirectory => Path.Combine(Root, "Responses");

    /// <summary>Where Linux, and a Debug build on macOS, keeps the key that seals settings blocks -
    /// see <see cref="FileSealingKeyStore"/>. Unused by a released macOS build (the Keychain) and on
    /// Windows (DPAPI).</summary>
    public static string SealingKeyFile => Path.Combine(Root, "sealing.key");

    public static string DebugLogFile => Path.Combine(Root, "debug.log");
}
