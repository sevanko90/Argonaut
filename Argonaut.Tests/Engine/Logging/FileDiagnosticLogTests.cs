using Argonaut.Engine.Logging;

namespace Argonaut.Tests.Engine.Logging;

/// <summary>
/// The file-backed diagnostic log: lines reach the file in order, a file already over the limit
/// is emptied when the log starts and one under it is left alone, and a log that cannot write
/// neither throws nor keeps queueing.
/// </summary>
public sealed class FileDiagnosticLogTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ArgonautTestFiles", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    private string LogPath => Path.Combine(dir, "debug.log");

    [Fact]
    public void Lines_ReachTheFile_InOrder_Stamped()
    {
        using (var log = new FileDiagnosticLog(LogPath))
        {
            log.Write("first");
            log.Write("second");
        }

        var lines = File.ReadAllLines(LogPath);
        Assert.Equal(2, lines.Length);
        Assert.Matches(@"^\d\d:\d\d:\d\d\.\d{3} first$", lines[0]);
        Assert.EndsWith(" second", lines[1]);
    }

    [Fact]
    public void AFileOverTheLimit_IsEmptied_WhenTheLogStarts()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(LogPath, new string('x', 200));

        using (var log = new FileDiagnosticLog(LogPath, maxBytes: 100))
            log.Write("fresh");

        var lines = File.ReadAllLines(LogPath);
        Assert.Single(lines);
        Assert.EndsWith(" fresh", lines[0]);
    }

    [Fact]
    public void AFileUnderTheLimit_IsAppendedTo()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(LogPath, "earlier\n");

        using (var log = new FileDiagnosticLog(LogPath, maxBytes: 100))
            log.Write("later");

        var lines = File.ReadAllLines(LogPath);
        Assert.Equal("earlier", lines[0]);
        Assert.EndsWith(" later", lines[1]);
    }

    /// <summary>A path that cannot be a file - its folder is a file - must not surface anywhere.</summary>
    [Fact]
    public void AnUnwritableLog_NeverThrows()
    {
        Directory.CreateDirectory(dir);
        string blocker = Path.Combine(dir, "not-a-folder");
        File.WriteAllText(blocker, "");

        var log = new FileDiagnosticLog(Path.Combine(blocker, "debug.log"));
        for (int i = 0; i < 1000; i++)
            log.Write("dropped");
        log.Dispose();
    }
}
