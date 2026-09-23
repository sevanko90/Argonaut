using System.Reflection;
using Argonaut.Shell.Updates;

namespace Argonaut;

/// <summary>
/// App identity constants: window titles, the About dialog and <c>UpdateService</c>.
/// </summary>
public static class AppInfo
{
    public const string Name = "Argonaut";
    public const string RepoUrl = "https://github.com/sevanko90/Argonaut";

    /// <summary>
    /// The embedded AssemblyInformationalVersion with any build-metadata suffix stripped (e.g.
    /// local dev builds embed "1.0.0+&lt;githash&gt;" since there's no release tag to derive
    /// from - only "1.0.0" is worth showing a user).
    /// </summary>
    public static string Version { get; } = ComputeVersion();

    private static string ComputeVersion()
    {
        string raw = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.0.0";

        int plusIndex = raw.IndexOf('+');
        return plusIndex >= 0 ? raw[..plusIndex] : raw;
    }
}
