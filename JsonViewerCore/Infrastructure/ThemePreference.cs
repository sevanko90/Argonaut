using System;
using System.IO;
using System.Text.Json;

namespace JsonViewerCore.Infrastructure;

public enum ThemeMode
{
    System,
    Light,
    Dark
}

public static class ThemePreference
{
    private static string SettingsFilePath => AppDataPaths.GetSettingsFilePath("theme.json");

    public static ThemeMode Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
                return ThemeMode.System;

            var json = File.ReadAllText(SettingsFilePath);
            var saved = JsonSerializer.Deserialize<SavedTheme>(json);
            return saved is not null && Enum.TryParse<ThemeMode>(saved.Mode, out var mode) ? mode : ThemeMode.System;
        }
        catch
        {
            return ThemeMode.System;
        }
    }

    public static void Save(ThemeMode mode)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath)!);
            File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(new SavedTheme(mode.ToString())));
        }
        catch
        {
            // Preference should not block theme switching.
        }
    }

    private sealed record SavedTheme(string Mode);
}
