using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Engine.Indexing;
using Argonaut.Features.Hex;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Features.Hex;

/// <summary>The hex document reads rows of sixteen straight from its source - across a source's
/// internal boundaries, as a growing source grows - and reads nothing once let go.</summary>
public sealed class HexViewModelTests
{
    private static byte[] Counting(int length) => Enumerable.Range(0, length).Select(i => (byte)i).ToArray();

    [Fact]
    public async Task Rows_AreSixteenBytes_AndTheLastHoldsWhatIsLeft()
    {
        using var origin = new MemoryByteOrigin(Counting(40), "bytes");
        using var hex = new HexViewModel();
        await hex.LoadAsync(origin);
        var row = new byte[16];

        Assert.Equal(16, hex.ReadRow(1, row));
        Assert.Equal(Counting(32)[16..], row);
        Assert.Equal(8, hex.ReadRow(2, row));
        Assert.Equal(0, hex.ReadRow(3, row));
        Assert.True(hex.CanHandleFileType(FileTypeDetector.FileKind.Hex));
        Assert.True(hex.IndexingTask.IsCompleted);
    }

    [Fact]
    public async Task ARowStraddlingASourcesBoundary_IsReadWhole()
    {
        using var origin = new SplitOrigin(Counting(64), pieceSize: 5);
        using var hex = new HexViewModel();
        await hex.LoadAsync(origin);
        var row = new byte[16];

        Assert.Equal(16, hex.ReadRow(2, row));
        Assert.Equal(Counting(48)[32..], row);
    }

    [Fact]
    public async Task AGrowingDocument_HasMoreRowsAsItArrives()
    {
        var source = new GrowingByteSource(Counting(100), initiallyAvailable: 20);
        using var origin = new GrowingByteOrigin(source);
        using var hex = new HexViewModel();
        await hex.LoadAsync(origin);

        Assert.Equal(2, HexRows.RowCount(hex.Length));
        source.Reveal(60);
        Assert.Equal(5, HexRows.RowCount(hex.Length));
        Assert.Equal(16, hex.ReadRow(4, new byte[16]));
    }

    [Fact]
    public async Task OnceLetGo_RowsReadEmpty()
    {
        using var origin = new MemoryByteOrigin(Counting(32), "bytes");
        var hex = new HexViewModel();
        await hex.LoadAsync(origin);

        hex.Dispose();

        Assert.Equal(0, hex.ReadRow(0, new byte[16]));
        Assert.Equal(0, hex.Length);
    }

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
