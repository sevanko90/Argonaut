using Argonaut.Engine.Bytes;
using Argonaut.Features.Hex;
using Argonaut.Features.Hex.Structs;
using Argonaut.Features.Hex.Values;

namespace Argonaut.Tests.Features.Hex.Structs;

public sealed class StructDecoderViewModelTests
{
    private const string Pair = "struct Pair { int16_t a; int16_t b; };";

    private static (StructDecoderViewModel Decoder, IByteSource Bytes) Decoder(string definition = Pair)
    {
        var bytes = new MemoryByteSource(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        var decoder = new StructDecoderViewModel(ByteOrder.Little) { DefinitionText = definition };
        decoder.Follow(bytes, 0, 0);
        return (decoder, bytes);
    }

    [Fact]
    public void WithNoDefinition_ThereIsNoTree_AndTheTabSaysWhatToDo()
    {
        var decoder = new StructDecoderViewModel(ByteOrder.Little);
        decoder.Follow(new MemoryByteSource(new byte[8]), 0, 0);

        Assert.Null(decoder.Tree);
        Assert.Contains("Definition tab", decoder.Message);
    }

    [Fact]
    public void ADefinition_DeclaresItsTypes_AndTheLastIsPicked()
    {
        var (decoder, _) = Decoder("struct In { char c; }; struct Out { In in; int x; };");

        Assert.Equal(["In", "Out"], decoder.Types);
        Assert.Equal("Out", decoder.SelectedType);
        Assert.Equal("Out", decoder.Tree!.Nodes.Struct.Name);
        Assert.Equal("C / C++ (detected)", decoder.LanguageText);
        Assert.False(decoder.HasMessage);
    }

    [Fact]
    public void MovingTheCaret_MovesTheSameTree()
    {
        var (decoder, _) = Decoder();
        var tree = decoder.Tree;
        int moves = 0;
        decoder.Moved += (_, _) => moves++;

        decoder.Follow(decoder.Tree!.Bytes, 10, 0);

        Assert.Same(tree, decoder.Tree);
        Assert.Equal(10, tree!.Start);
        Assert.Equal(1, moves);
        Assert.Equal("Pair · 4 bytes, align 2 · at 0xA", decoder.SummaryText);
    }

    [Fact]
    public void ASelectionOfTwoOrMore_ReadsAsAnArray_InANewTree()
    {
        var (decoder, bytes) = Decoder();
        var single = decoder.Tree;
        int closed = 0;
        single!.Closing += (_, _) => closed++;

        decoder.Follow(bytes, 8, 13);

        Assert.NotSame(single, decoder.Tree);
        Assert.Equal(1, closed);
        Assert.Equal(3, decoder.Tree!.Nodes.Count);
        Assert.Equal("Pair[3] · 3 × 4 bytes, align 2 · at 0x8", decoder.SummaryText);

        decoder.Follow(bytes, 8, 7);
        Assert.Equal(1, decoder.Tree!.Nodes.Count);
    }

    [Fact]
    public void Pinned_TheStructStays_AndUnpinningCatchesUp()
    {
        var (decoder, bytes) = Decoder();
        decoder.Follow(bytes, 4, 0);
        decoder.IsPinned = true;

        decoder.Follow(bytes, 20, 0);
        Assert.Equal(4, decoder.Tree!.Start);

        decoder.IsPinned = false;
        Assert.Equal(20, decoder.Tree!.Start);
    }

    [Fact]
    public void TheByteOrder_RereadsTheSameTree()
    {
        var (decoder, _) = Decoder();
        var tree = decoder.Tree;
        int moves = 0;
        decoder.Moved += (_, _) => moves++;

        decoder.Order = ByteOrder.Big;

        Assert.Same(tree, decoder.Tree);
        Assert.Equal(ByteOrder.Big, tree!.Order);
        Assert.Equal(1, moves);
    }

    [Fact]
    public void Problems_AreListed_WithWhereTheyAre()
    {
        var (decoder, _) = Decoder("struct A { int x; Mystery m; virtual void f(); };");

        Assert.Equal(["1:30  A virtual member adds a hidden vtable pointer, which is not laid out - remove it to read the fields."], decoder.Problems);
        Assert.Null(decoder.Tree);
        Assert.StartsWith("1:27  A.m: unknown type Mystery", decoder.Message);
    }

    [Fact]
    public void TheTarget_OnlyShowsForC_AndChangesTheLayout()
    {
        var (decoder, _) = Decoder("struct L { long value; };");
        Assert.True(decoder.ShowsTarget);

        decoder.Target = StructTarget.Lp64;
        Assert.Equal(8, decoder.Tree!.Nodes.Struct.Size);
        decoder.Target = StructTarget.Llp64;
        Assert.Equal(4, decoder.Tree!.Nodes.Struct.Size);

        decoder.DefinitionText = "struct L { public long Value; }";
        Assert.False(decoder.ShowsTarget);
    }

    [Fact]
    public void Closing_LetsTheTreeGo()
    {
        var (decoder, _) = Decoder();
        int closed = 0;
        decoder.Tree!.Closing += (_, _) => closed++;

        decoder.Close();

        Assert.Null(decoder.Tree);
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task TheHexView_FeedsTheCaretAndByteOrder_AndClosesTheTreeBeforeReleasingItsBytes()
    {
        using var origin = new MemoryByteOrigin(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray(), "bytes");
        var hex = new HexViewModel();
        await hex.LoadAsync(origin);
        hex.Structs.DefinitionText = Pair;

        hex.Cursor.Place(6);
        Assert.Equal(6, hex.Structs.Tree!.Start);

        hex.Values.Order = hex.Values.Order == ByteOrder.Little ? ByteOrder.Big : ByteOrder.Little;
        Assert.Equal(hex.Values.Order, hex.Structs.Tree!.Order);

        int closed = 0;
        hex.Structs.Tree!.Closing += (_, _) => closed++;
        hex.Dispose();
        Assert.Equal(1, closed);
        Assert.Null(hex.Structs.Tree);
    }
}
