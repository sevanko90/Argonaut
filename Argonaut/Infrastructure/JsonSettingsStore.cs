using System.IO;
using System.Text.Json;

namespace Argonaut.Infrastructure;

/// <summary>
/// Load/save helper for the small JSON settings files under <see cref="AppDataPaths"/>.
/// Persistence is best-effort by design: any IO/parse failure yields null (load) or is
/// swallowed (save/delete), because settings must never block opening a file or switching
/// theme.
/// </summary>
public static class JsonSettingsStore
{
    public static T? TryLoad<T>(string fileName, JsonSerializerOptions? options = null) where T : class
    {
        try
        {
            string path = AppDataPaths.GetSettingsFilePath(fileName);
            if (!File.Exists(path))
                return null;

            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), options);
        }
        catch
        {
            return null;
        }
    }

    public static void Save<T>(string fileName, T value, JsonSerializerOptions? options = null)
    {
        try
        {
            string path = AppDataPaths.GetSettingsFilePath(fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value, options));
        }
        catch
        {
            // Best-effort persistence - see class remarks.
        }
    }

    public static void Delete(string fileName)
    {
        try
        {
            File.Delete(AppDataPaths.GetSettingsFilePath(fileName));
        }
        catch
        {
            // Best-effort persistence - see class remarks.
        }
    }
}
