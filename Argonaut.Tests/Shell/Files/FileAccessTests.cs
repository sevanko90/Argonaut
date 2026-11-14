using Argonaut.Engine.Bytes;
using Argonaut.Shell.Files;

namespace Argonaut.Tests.Shell.Files;

/// <summary>
/// Describing a file as a locator and opening it again: plainly by path everywhere, and through a
/// bookmark on macOS, which is what still finds a file the user moved.
/// </summary>
public sealed class FileAccessTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "ArgonautTests", Guid.NewGuid().ToString("N"));

    public FileAccessTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    private string Write(string name)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, "{}");
        return path;
    }

    [Fact]
    public void Plain_DescribesByPath_AndOpensThatPath()
    {
        string path = Write("a.json");
        var access = new PlainFileAccess();

        var locator = access.Describe(path);
        using var grant = access.Open(locator);

        Assert.Null(locator.Bookmark);
        Assert.Equal(Path.GetFullPath(path), grant.Path);
        Assert.Same(locator, grant.Locator);
    }

    [Fact]
    public void ForThisPlatform_IsTheBookmarkingOneOnMacOS()
    {
        var access = FileAccesses.ForThisPlatform();

        Assert.IsType(OperatingSystem.IsMacOS() ? typeof(MacFileAccess) : typeof(PlainFileAccess), access);
    }

    [Fact]
    public void Mac_DescribesWithABookmark_AndOpensThroughIt()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        string path = Write("a.json");
        var access = new MacFileAccess();

        var locator = access.Describe(path);
        using var grant = access.Open(locator);

        Assert.NotNull(locator.Bookmark);
        Assert.Equal(Path.GetFullPath(path), grant.Path);
        Assert.True(locator.IsSamePlace(grant.Locator));
    }

    [Fact]
    public void Mac_FindsAMovedFile_ThroughItsBookmark()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        string path = Write("before.json");
        var access = new MacFileAccess();
        var locator = access.Describe(path);
        string moved = Path.Combine(folder, "after.json");
        File.Move(path, moved);

        using var grant = access.Open(locator);

        Assert.Equal("after.json", Path.GetFileName(grant.Path));
        Assert.False(locator.IsSamePlace(grant.Locator));
        Assert.Equal("after.json", Path.GetFileName(grant.Locator.Path));
        Assert.NotNull(grant.Locator.Bookmark);
    }

    [Fact]
    public void Mac_ABookmarkThatWillNotResolve_FallsBackToThePath()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        string path = Write("a.json");
        var access = new MacFileAccess();
        var broken = new FileLocator(path, bookmark: [1, 2, 3, 4]);

        using var grant = access.Open(broken);

        Assert.Equal(Path.GetFullPath(path), grant.Path);
    }

    [Fact]
    public void AGrant_IsReleasedOnce_WithTheOriginHoldingIt()
    {
        string path = Write("a.json");
        int released = 0;
        var grant = new FileAccessGrant(new FileLocator(path), () => released++);

        var origin = new FileByteOrigin(grant);
        origin.Dispose();
        origin.Dispose();

        Assert.Equal(1, released);
    }
}
