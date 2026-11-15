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
    public async Task TheStatusStrip_DescribesTheByte_AndWhereItIs()
    {
        using var origin = new MemoryByteOrigin([0x00, (byte)'A', 0xC3], "bytes");
        using var hex = new HexViewModel();
        await hex.LoadAsync(origin);

        hex.ShowByte(1);
        Assert.Equal("0x41 · 65 · 0100 0001 · U+0041 LATIN CAPITAL LETTER A", hex.ByteValueText);
        Assert.Equal("Byte 1    0x00000001", hex.BytePositionText);

        // A byte above 0x7F is no character on its own.
        hex.ShowByte(2);
        Assert.Equal("0xC3 · 195 · 1100 0011", hex.ByteValueText);

        hex.ShowByte(null);
        Assert.Equal(string.Empty, hex.ByteValueText);
        Assert.Equal(string.Empty, hex.BytePositionText);

        hex.ShowByte(3);
        Assert.Null(hex.ShownOffset);
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
