using Argonaut.Engine.Locations;

namespace Argonaut.Tests.Engine.Locations;

public sealed class ConfigLocationSourceTests
{
    private static ConfigLocationSource Source(ConfigPlatform platform, string home,
        Dictionary<string, string>? environment = null, params string[] existing)
    {
        environment ??= [];
        var folders = new HashSet<string>(existing);
        return new ConfigLocationSource(platform, home,
            name => environment.GetValueOrDefault(name), folders.Contains);
    }

    [Fact]
    public void Windows_OffersTheAppDataFoldersFromTheEnvironment()
    {
        var source = Source(ConfigPlatform.Windows, @"C:\Users\me",
            new() { ["APPDATA"] = @"C:\Users\me\AppData\Roaming", ["LOCALAPPDATA"] = @"C:\Users\me\AppData\Local" },
            @"C:\Users\me", @"C:\Users\me\AppData\Roaming", @"C:\Users\me\AppData\Local");

        Assert.Equal(["Home", "Roaming app data", "Local app data"], source.GetExisting().Select(l => l.Name));
    }

    [Fact]
    public void Linux_UsesXdgConfigHomeWhenSetAndAbsolute()
    {
        var source = Source(ConfigPlatform.Linux, "/home/me", new() { ["XDG_CONFIG_HOME"] = "/custom/cfg" },
            "/home/me", "/custom/cfg", "/home/me/.config", "/etc");

        var config = Assert.Single(source.GetExisting(), l => l.Name == "Config");
        Assert.Equal("/custom/cfg", config.Path);
    }

    [Fact]
    public void Linux_FallsBackToDotConfigWhenXdgIsRelativeOrUnset()
    {
        var relative = Source(ConfigPlatform.Linux, "/home/me", new() { ["XDG_CONFIG_HOME"] = "cfg" },
            "/home/me", "/home/me/.config");
        var unset = Source(ConfigPlatform.Linux, "/home/me", existing: ["/home/me", "/home/me/.config"]);

        Assert.Equal("/home/me/.config", Assert.Single(relative.GetExisting(), l => l.Name == "Config").Path);
        Assert.Equal("/home/me/.config", Assert.Single(unset.GetExisting(), l => l.Name == "Config").Path);
    }

    [Fact]
    public void MacOs_OffersLibraryFoldersAndEtc()
    {
        var source = Source(ConfigPlatform.MacOs, "/Users/me", existing:
            ["/Users/me", "/Users/me/.config", "/Users/me/Library/Application Support",
             "/Users/me/Library/Preferences", "/etc"]);

        Assert.Equal(["Home", "Config", "Application Support", "Preferences", "System config"],
            source.GetExisting().Select(l => l.Name));
    }

    [Fact]
    public void FoldersThatDoNotExistAreLeftOut()
    {
        var source = Source(ConfigPlatform.Linux, "/home/me", existing: ["/home/me"]);

        Assert.Equal(["Home"], source.GetExisting().Select(l => l.Name));
    }

    [Fact]
    public void TheSameFolderIsOfferedOnce()
    {
        // Windows paths compare ignoring case, so an environment variable spelling the home
        // folder differently must not list it twice.
        var source = Source(ConfigPlatform.Windows, @"C:\Users\me",
            new() { ["APPDATA"] = @"c:\users\me" }, @"C:\Users\me", @"c:\users\me");

        Assert.Single(source.GetExisting());
    }
}
