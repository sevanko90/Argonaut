using System.Text;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// The platform half of a save: staging beside the destination, committing by replace, and
/// never leaving the destination half-written. Runs against the real file system of whichever
/// platform the suite is on, so the Windows <c>File.Replace</c> and Unix <c>rename</c> branches
/// are each covered on their own CI leg rather than here together.
/// </summary>
public sealed class SiblingFileReplacerTests : IDisposable
{
    private readonly string tempDir;
    private readonly SiblingFileReplacer replacer = SiblingFileReplacer.ForCurrentPlatform();

    public SiblingFileReplacerTests()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "ArgonautTestFiles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(tempDir, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static void Write(StagedFile staged, string content) => staged.Content.Write(Encoding.UTF8.GetBytes(content));

    private string[] Stages() => Directory.GetFiles(tempDir, $"*{SiblingFileReplacer.StageMarker}*");

    [Fact]
    public void ForCurrentPlatform_PicksThisPlatformsReplacer()
    {
        var expected = OperatingSystem.IsWindows() ? typeof(WindowsFileReplacer)
            : OperatingSystem.IsMacOS() ? typeof(MacFileReplacer)
            : typeof(UnixFileReplacer);

        Assert.IsType(expected, replacer);
    }

    [Fact]
    public void Commit_ReplacesTheDestinationsContent()
    {
        string path = WriteFile("doc.txt", "old content");

        using (var staged = replacer.Stage(new FileByteOrigin(path), 11))
        {
            Write(staged, "new content");
            staged.Commit();
        }

        Assert.Equal("new content", File.ReadAllText(path));
        Assert.Empty(Stages());
    }

    [Fact]
    public void Commit_CreatesADestinationThatDidNotExist()
    {
        string path = Path.Combine(tempDir, "new.txt");

        using (var staged = replacer.Stage(new FileByteOrigin(path), 5))
        {
            Write(staged, "hello");
            staged.Commit();
        }

        Assert.Equal("hello", File.ReadAllText(path));
    }

    [Fact]
    public void TheStageIsBesideTheDestination_AndHidden()
    {
        string path = WriteFile("doc.txt", "x");

        using var staged = replacer.Stage(new FileByteOrigin(path), 1);

        Assert.Equal(tempDir, Path.GetDirectoryName(staged.Location));
        Assert.StartsWith(".doc.txt" + SiblingFileReplacer.StageMarker, Path.GetFileName(staged.Location));
    }

    [Fact]
    public void DisposingWithoutCommitting_DeletesTheStage_AndLeavesTheDestination()
    {
        string path = WriteFile("doc.txt", "original");

        using (var staged = replacer.Stage(new FileByteOrigin(path), 3))
            Write(staged, "new");

        Assert.Equal("original", File.ReadAllText(path));
        Assert.Empty(Stages());
    }

    [Fact]
    public void KeepStagedContent_SurvivesDispose()
    {
        string path = WriteFile("doc.txt", "original");

        string location;
        using (var staged = replacer.Stage(new FileByteOrigin(path), 5))
        {
            Write(staged, "kept!");
            staged.Seal();
            staged.KeepStagedContent();
            location = staged.Location;
        }

        Assert.Equal("kept!", File.ReadAllText(location));
    }

    [Fact]
    public void Stage_RefusesADocumentWithNoPath()
    {
        var paste = new MemoryByteOrigin(Encoding.UTF8.GetBytes("x"), "Pasted text");

        Assert.Throws<ArgumentException>(() => replacer.Stage(paste, 1));
    }

    [Fact]
    public void Stage_RefusesMoreThanTheVolumeHas()
    {
        string path = WriteFile("doc.txt", "x");

        var failure = Assert.Throws<IOException>(() => replacer.Stage(new FileByteOrigin(path), long.MaxValue / 2));

        Assert.Contains("free space", failure.Message);
        Assert.Empty(Stages());
    }

    [Fact]
    public void Stage_SweepsOnlyOldAbandonedStagesOfTheSameFile()
    {
        string path = WriteFile("doc.txt", "x");
        string old = WriteFile($".doc.txt{SiblingFileReplacer.StageMarker}old", "crashed save");
        string fresh = WriteFile($".doc.txt{SiblingFileReplacer.StageMarker}fresh", "save in progress");
        string otherFile = WriteFile($".other.txt{SiblingFileReplacer.StageMarker}old", "someone else's");
        var longAgo = DateTime.UtcNow - SiblingFileReplacer.AbandonedAfter - TimeSpan.FromMinutes(5);
        File.SetLastWriteTimeUtc(old, longAgo);
        File.SetLastWriteTimeUtc(otherFile, longAgo);

        using (replacer.Stage(new FileByteOrigin(path), 1))
        {
        }

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(otherFile));
    }

    [Fact]
    public void Commit_KeepsTheDestinationsPermissions()
    {
        if (OperatingSystem.IsWindows())
            return; // ReplaceFile keeps ACLs; mode bits are a Unix concern

        string path = WriteFile("script.sh", "echo old");
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead;
        File.SetUnixFileMode(path, mode);

        using (var staged = replacer.Stage(new FileByteOrigin(path), 8))
        {
            Write(staged, "echo new");
            staged.Commit();
        }

        Assert.Equal(mode, File.GetUnixFileMode(path));
    }

    [Fact]
    public void Commit_ThroughASymlink_ReplacesTheTarget_AndKeepsTheLink()
    {
        string target = WriteFile("real.txt", "old");
        string link = Path.Combine(tempDir, "link.txt");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // Windows without the symlink privilege
        }

        using (var staged = replacer.Stage(new FileByteOrigin(link), 3))
        {
            Write(staged, "new");
            staged.Commit();
        }

        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal("new", File.ReadAllText(target));
    }
}
