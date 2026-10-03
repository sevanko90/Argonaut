using System;
using System.Collections.Generic;
using System.IO;

namespace Argonaut.Engine.Locations;

/// <summary>The operating systems whose configuration layouts <see cref="ConfigLocationSource"/> knows.</summary>
public enum ConfigPlatform
{
    Windows,
    MacOs,
    Linux,
}

/// <summary>
/// The conventional configuration folders for one platform. Everything it reads from the machine
/// - the home folder, environment variables, which folders exist - is handed in, so the
/// composition root decides where it comes from and tests need no file system.
/// </summary>
public sealed class ConfigLocationSource : IConfigLocationSource
{
    private readonly ConfigPlatform platform;
    private readonly string homeFolder;
    private readonly Func<string, string?> environmentVariable;
    private readonly Func<string, bool> folderExists;

    public ConfigLocationSource(ConfigPlatform platform, string homeFolder,
        Func<string, string?> environmentVariable, Func<string, bool> folderExists)
    {
        this.platform = platform;
        this.homeFolder = homeFolder;
        this.environmentVariable = environmentVariable;
        this.folderExists = folderExists;
    }

    /// <summary>The source for the platform the app is running on, reading the real machine.</summary>
    public static ConfigLocationSource ForCurrentPlatform() => new(
        OperatingSystem.IsWindows() ? ConfigPlatform.Windows
            : OperatingSystem.IsMacOS() ? ConfigPlatform.MacOs
            : ConfigPlatform.Linux,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetEnvironmentVariable,
        Directory.Exists);

    public IReadOnlyList<ConfigLocation> GetExisting()
    {
        var found = new List<ConfigLocation>();
        var seen = new HashSet<string>(PathComparer);

        void Offer(string name, string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !seen.Add(path) || !folderExists(path))
                return;
            found.Add(new ConfigLocation(name, path));
        }

        Offer("Home", homeFolder);

        switch (platform)
        {
            case ConfigPlatform.Windows:
                Offer("Roaming app data", environmentVariable("APPDATA"));
                Offer("Local app data", environmentVariable("LOCALAPPDATA"));
                Offer("Program data", environmentVariable("ProgramData"));
                break;

            case ConfigPlatform.MacOs:
                Offer("Config", XdgConfigHome());
                Offer("Application Support", Path.Combine(homeFolder, "Library", "Application Support"));
                Offer("Preferences", Path.Combine(homeFolder, "Library", "Preferences"));
                Offer("System config", "/etc");
                break;

            default:
                Offer("Config", XdgConfigHome());
                Offer("System config", "/etc");
                break;
        }

        return found;
    }

    // XDG_CONFIG_HOME applies when set and absolute, otherwise the spec's ~/.config.
    private string XdgConfigHome() =>
        environmentVariable("XDG_CONFIG_HOME") is { } configured && Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(homeFolder, ".config");

    private StringComparer PathComparer =>
        platform == ConfigPlatform.Linux ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
}
