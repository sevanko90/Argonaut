using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing;
using Argonaut.Engine.Saving;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Engine.Saving;

/// <summary>
/// Copying a document's bytes out unchanged - from any origin, across a source's internal
/// boundaries - and staging a file another app can open, under the name that picks the app.
/// </summary>
public sealed class OriginExporterTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ArgonautTests", Guid.NewGuid().ToString("N"));

    public OriginExporterTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    private string OpenFolder => Path.Combine(root, "Opened");

    [Fact]
    public async Task ACopy_HoldsEveryByte_EvenOverASourceThatSplitsItsReads()
    {
        var bytes = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray();
        using var origin = new SplitOrigin(bytes, pieceSize: 333);
        string destination = Path.Combine(root, "copy.bin");

        await new OriginExporter(OpenFolder).CopyAsync(origin, destination);

        Assert.Equal(bytes, File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task ACopy_ReplacesWhatWasThere()
    {
        string destination = Path.Combine(root, "copy.txt");
        File.WriteAllText(destination, "a much longer old file");
        using var origin = new MemoryByteOrigin("new"u8.ToArray(), "pasted");

        await new OriginExporter(OpenFolder).CopyAsync(origin, destination);

        Assert.Equal("new", File.ReadAllText(destination));
    }

    [Fact]
    public async Task CopyingAFileOverItself_LeavesItAlone()
    {
        string path = Path.Combine(root, "same.json");
        File.WriteAllText(path, "{}");
        using var origin = new FileByteOrigin(path);

        await new OriginExporter(OpenFolder).CopyAsync(origin, path);

        Assert.Equal("{}", File.ReadAllText(path));
    }

    [Fact]
    public async Task OpeningAFile_HandsOverTheFileItself()
    {
        string path = Path.Combine(root, "photo.png");
        File.WriteAllBytes(path, [1, 2, 3]);
        using var origin = new FileByteOrigin(path);

        Assert.Equal(path, await new OriginExporter(OpenFolder).PrepareToOpenAsync(origin, "ignored.png"));
        Assert.False(Directory.Exists(OpenFolder));
    }

    [Fact]
    public async Task OpeningWhatHasNoFile_StagesACopyUnderTheNameGiven_AndTheNextStartSweepsIt()
    {
        using var origin = new MemoryByteOrigin([9, 8, 7], "pasted");
        var exporter = new OriginExporter(OpenFolder);

        string staged = await exporter.PrepareToOpenAsync(origin, "logo.png");

        Assert.Equal("logo.png", Path.GetFileName(staged));
        Assert.StartsWith(OpenFolder, staged);
        Assert.Equal([9, 8, 7], File.ReadAllBytes(staged));

        new OriginExporter(OpenFolder).Sweep();
        Assert.Empty(Directory.GetFileSystemEntries(OpenFolder));
    }

    /// <summary>An origin whose source serves its bytes in pieces - see <see cref="SplitByteSource"/>.</summary>
    private sealed class SplitOrigin(byte[] bytes, int pieceSize) : IByteOrigin
    {
        public string DisplayName => "split";

        public SourceLocator? Locator => null;

        public long AvailableLength => bytes.Length;

        public IByteSource Open() => new SplitByteSource(bytes, pieceSize);

        public IByteSource OpenRange(long offset, long length) => throw new NotSupportedException();

        public KeptIndexes KeptIndexes { get; } = new();

        public void Dispose()
        {
        }
    }
}
