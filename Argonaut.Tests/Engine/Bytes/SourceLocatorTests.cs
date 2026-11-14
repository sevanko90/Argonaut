using Argonaut.Engine.Bytes;

namespace Argonaut.Tests.Engine.Bytes;

/// <summary>What a locator names, how it is shown, and when two name the same place.</summary>
public sealed class SourceLocatorTests
{
    [Fact]
    public void AFileLocator_IsAbsolute_AndNamedForTheFile()
    {
        var locator = new FileLocator(Path.Combine("folder", "data.json"));

        Assert.True(Path.IsPathFullyQualified(locator.Path));
        Assert.Equal("data.json", locator.DefaultName);
        Assert.Equal(locator.Path, locator.DisplayText);
    }

    [Fact]
    public void TheSameFileByAnotherSpelling_IsTheSamePlace()
    {
        string folder = Path.GetTempPath();
        var plain = new FileLocator(Path.Combine(folder, "data.json"));
        var roundabout = new FileLocator(Path.Combine(folder, "sub", "..", "data.json"));

        Assert.True(plain.IsSamePlace(roundabout));
    }

    [Fact]
    public void FileIdentity_FollowsThePlatformsUsualCaseRule()
    {
        string folder = Path.GetTempPath();
        var lower = new FileLocator(Path.Combine(folder, "data.json"));
        var upper = new FileLocator(Path.Combine(folder, "DATA.json"));

        Assert.Equal(!OperatingSystem.IsLinux(), lower.IsSamePlace(upper));
    }

    [Fact]
    public void DifferentFiles_AreDifferentPlaces_AndNothingIsTheSameAsNoLocator()
    {
        string folder = Path.GetTempPath();
        var a = new FileLocator(Path.Combine(folder, "a.json"));

        Assert.False(a.IsSamePlace(new FileLocator(Path.Combine(folder, "b.json"))));
        Assert.False(a.IsSamePlace(null));
    }

    [Fact]
    public void LocationText_IsTheLocatorsText_OrTheDisplayNameWithoutOne()
    {
        string path = Path.Combine(Path.GetTempPath(), "data.json");
        using var file = new FileByteOrigin(path);
        using var paste = new MemoryByteOrigin([1, 2, 3], "Pasted text");

        Assert.Equal(Path.GetFullPath(path), file.LocationText());
        Assert.Equal("Pasted text", paste.LocationText());
    }
}
