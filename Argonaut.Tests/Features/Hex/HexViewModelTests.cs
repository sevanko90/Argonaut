using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Engine.Indexing;
using Argonaut.Features.Hex;
using Argonaut.Ui.Documents.Navigation;
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
    public async Task TheStatusStrip_SaysWhereTheCaretIs_InDecimalAndHex_AndHowMuchIsSelected()
    {
        using var origin = new MemoryByteOrigin(Counting(5000), "bytes");
        using var hex = new HexViewModel();
        await hex.LoadAsync(origin);
        var changed = new List<string?>();
        hex.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal("Byte 0    0x00000000", hex.CaretPositionText);
        Assert.Equal(string.Empty, hex.CaretSelectionText);

        hex.Cursor.Place(1234);
        Assert.Equal("Byte 1,234    0x000004D2", hex.CaretPositionText);
        Assert.Contains(nameof(HexViewModel.CaretPositionText), changed);

        // Taking a selection forward, the caret is on its last byte.
        hex.Cursor.Move(HexMove.Right, extend: true, pageRows: 10);
        hex.Cursor.Move(HexMove.Right, extend: true, pageRows: 10);
        Assert.Equal("Byte 1,235    0x000004D3", hex.CaretPositionText);
        Assert.Equal("2 bytes", hex.CaretSelectionText);
    }

    [Fact]
    public async Task AnEmptyDocument_HasNoCaret()
    {
        using var origin = new MemoryByteOrigin([], "bytes");
        using var hex = new HexViewModel();
        await hex.LoadAsync(origin);

        Assert.Null(hex.Cursor.Caret);
        Assert.Equal(string.Empty, hex.CaretPositionText);
        Assert.Null(hex.SelectedByteRange);
    }

    [Fact]
    public async Task ARevealedRange_IsSelected_WithTheCaretAtItsStart_AndIsWhereTheUserIs()
    {
        using var origin = new MemoryByteOrigin(Counting(5000), "bytes");
        using var hex = new HexViewModel();
        await hex.LoadAsync(origin);

        await hex.RevealByteRangeAsync(new ByteRange(100, 20));

        Assert.Equal(new HexCaret(120, 100), hex.Cursor.Caret);
        Assert.Equal(new ByteRange(100, 20), hex.SelectedByteRange);

        await hex.RevealByteRangeAsync(ByteRange.At(7));
        Assert.Equal(ByteRange.At(7), hex.SelectedByteRange);
    }

    [Fact]
    public async Task TheSelection_IsReadForACopy_UpToTheLimit()
    {
        using var origin = new SplitOrigin(Counting(64), pieceSize: 5);
        using var hex = new HexViewModel();
        await hex.LoadAsync(origin);

        Assert.False(hex.TryReadSelection(out _));

        hex.Cursor.Select(new HexCaret(30, 10));
        Assert.True(hex.TryReadSelection(out byte[] bytes));
        Assert.Equal(Counting(30)[10..], bytes);
    }

    [Fact]
    public async Task ASelectionLargerThanACopyTakes_IsRefused()
    {
        using var origin = new MemoryByteOrigin(new byte[HexCopy.MaxBytes + 1], "bytes");
        using var hex = new HexViewModel();
        await hex.LoadAsync(origin);

        hex.Cursor.SelectAll();
        Assert.False(hex.TryReadSelection(out _));
    }

    [Fact]
    public async Task GoToOffset_PutsTheCaretThere_FromAnEntryThatStartsAtTheCaret()
    {
        using var origin = new MemoryByteOrigin(Counting(5000), "bytes");
        using var hex = new HexViewModel();
        await hex.LoadAsync(origin);
        hex.Cursor.Place(0x4D2);
        Assert.Equal("0x4D2", hex.GoToStartText);

        Assert.Null(hex.GoToOffset("0x100"));
        Assert.Equal(HexCaret.At(0x100), hex.Cursor.Caret);
        Assert.True(hex.Cursor.TakeReveal());

        Assert.Null(hex.GoToOffset("+16"));
        Assert.Equal(HexCaret.At(0x110), hex.Cursor.Caret);
    }

    [Fact]
    public async Task GoToOffset_SaysWhatIsWrong_AndLeavesTheCaret()
    {
        using var origin = new MemoryByteOrigin(Counting(5000), "bytes");
        using var hex = new HexViewModel();
        await hex.LoadAsync(origin);
        hex.Cursor.Place(7);

        Assert.Equal("Not an offset. Type a decimal number, or hex like 0x1F40.", hex.GoToOffset("zz"));
        Assert.Equal("0x1388 is past the end. The last byte is 0x1387 (4,999).", hex.GoToOffset("5000"));
        Assert.Equal(HexCaret.At(7), hex.Cursor.Caret);
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

    [Fact]
    public async Task TheStatusStrip_NamesTheCharacterTheCaretIsIn()
    {
        using var origin = new MemoryByteOrigin([(byte)'A', 0xC3, 0xA9, 0xFF], "bytes");
        using var hex = new HexViewModel();
        await hex.LoadAsync(origin);

        Assert.Equal("U+0041 LATIN CAPITAL LETTER A", hex.CaretCharacterText);

        // Either byte of the é names it.
        hex.Cursor.Place(2);
        Assert.Equal("U+00E9 LATIN SMALL LETTER E WITH ACUTE", hex.CaretCharacterText);

        hex.Cursor.Place(3);
        Assert.Equal("0xFF not UTF-8", hex.CaretCharacterText);
    }

    [Fact]
    public async Task AStreamedDocument_ShowsWhatArrives_AndStopsFollowingWhenItSettles()
    {
        var source = new GrowingByteSource(Counting(100), initiallyAvailable: 20);
        using var origin = new GrowingByteOrigin(source);
        using var hex = new HexViewModel();
        int grew = 0;
        hex.Grew += (_, _) => grew++;
        await hex.LoadAsync(origin);
        Assert.Equal("Hex · 20 bytes", hex.StatusDetail);

        source.Reveal(30);
        source.Seal();
        await hex.GrowthFollowed!;

        Assert.Equal(100, hex.Length);
        Assert.Equal("Hex · 100 bytes", hex.StatusDetail);
        Assert.True(grew >= 1);
    }

    [Fact]
    public async Task ADocumentWholeWhenItOpens_FollowsNothing()
    {
        using var origin = new MemoryByteOrigin(Counting(10), "bytes");
        using var hex = new HexViewModel();
        await hex.LoadAsync(origin);

        Assert.Null(hex.GrowthFollowed);
    }

    [Fact]
    public async Task ClosingWhileAStreamIsStillArriving_StopsWaitingForIt()
    {
        var source = new GrowingByteSource(Counting(100), initiallyAvailable: 20);
        using var origin = new GrowingByteOrigin(source);
        var hex = new HexViewModel();
        await hex.LoadAsync(origin);

        var closing = Task.Run(hex.Dispose);
        Assert.Same(closing, await Task.WhenAny(closing, Task.Delay(TimeSpan.FromSeconds(5))));
        Assert.Equal(0, hex.Length);
    }
}
