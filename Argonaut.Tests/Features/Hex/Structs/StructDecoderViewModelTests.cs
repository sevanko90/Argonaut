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
        Assert.Contains("Structures tab", decoder.Message);
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
        Assert.Equal("Pair", decoder.StructTitle);
        Assert.Equal("4 bytes · align 2 · at 0xA", decoder.StructDetail);
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
        Assert.Equal("Pair[3]", decoder.StructTitle);
        Assert.Equal("3 × 4 bytes · align 2 · at 0x8", decoder.StructDetail);

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
    public void ANewPaste_TellsTheLanguageAgain()
    {
        var (decoder, _) = Decoder();
        decoder.Language = StructLanguageChoice.CSharp;

        decoder.DefinitionPasted();
        decoder.DefinitionText = Pair;

        Assert.Equal(StructLanguageChoice.Detect, decoder.Language);
        Assert.Equal(0, decoder.SelectedLanguageIndex);
        Assert.Equal("C / C++ (detected)", decoder.LanguageText);
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

    [Theory]
    [InlineData(StructLanguage.CSharp, "C# (detected)")]
    [InlineData(StructLanguage.C, "C / C++ (detected)")]
    public void TheSample_FillsTheEmptyEditor_AndLaysOutTo268Bytes_WithItsNameAt9(StructLanguage language, string read)
    {
        var decoder = new StructDecoderViewModel(ByteOrder.Little);
        decoder.Follow(new MemoryByteSource(new byte[300]), 0, 0);
        Assert.True(decoder.OffersSample);

        decoder.InsertSample(language);

        Assert.False(decoder.OffersSample);
        Assert.False(decoder.HasProblems, string.Join("; ", decoder.Problems));
        Assert.Equal(read, decoder.LanguageText);
        var laid = decoder.Tree!.Nodes.Struct;
        Assert.Equal(268, laid.Size);
        Assert.Equal(4, laid.Alignment);
        Assert.Equal(9, laid.Fields.Single(f => f.Name.Equals("name", StringComparison.OrdinalIgnoreCase)).Offset);
    }

    [Fact]
    public void TheCSample_AfterCSharpWasChosen_IsReadAsC()
    {
        var decoder = new StructDecoderViewModel(ByteOrder.Little) { Language = StructLanguageChoice.CSharp };
        decoder.InsertSample(StructLanguage.C);
        Assert.Equal(StructLanguageChoice.Detect, decoder.Language);
        Assert.Equal("C / C++ (detected)", decoder.LanguageText);
        Assert.False(decoder.HasProblems);
    }

    private const string Shaped = "struct Shaped { int16_t a; uint8_t bytes[4]; };";

    [Fact]
    public void TheMappedRange_IsTheStruct_OrTheWholeArray()
    {
        var (decoder, bytes) = Decoder();
        decoder.Follow(bytes, 6, 0);
        Assert.Equal(new ByteRange(6, 4), decoder.MappedRange);

        decoder.Follow(bytes, 8, 12);
        Assert.Equal(new ByteRange(8, 12), decoder.MappedRange);
    }

    [Fact]
    public void AHoveredRow_MarksItsFieldsBytes_UntilThePointerLeaves()
    {
        var (decoder, bytes) = Decoder();
        decoder.Follow(bytes, 10, 0);
        var b = decoder.Tree!.Nodes.Child(decoder.Tree.Nodes.Root, 1);

        decoder.Hover(b);
        Assert.Equal(new ByteRange(12, 2), decoder.HighlightedField);

        decoder.Hover(null);
        Assert.Null(decoder.HighlightedField);
    }

    [Fact]
    public void ARowSelectedInTheTree_SelectsItsBytes_WithoutTheStructFollowingTheCaretThere()
    {
        var (decoder, bytes) = Decoder();
        decoder.Follow(bytes, 10, 0);
        var b = decoder.Tree!.Nodes.Child(decoder.Tree.Nodes.Root, 1);

        var range = decoder.SelectField(b);
        decoder.Follow(bytes, range.Offset, range.Length);

        Assert.Equal(new ByteRange(12, 2), range);
        Assert.Equal(10, decoder.Tree.Start);

        // The next caret move is the user's again.
        decoder.Follow(bytes, 20, 0);
        Assert.Equal(20, decoder.Tree.Start);
    }

    [Fact]
    public void Pinned_TheCaretsFieldIsTheDeepestHoldingIt_AndMarked()
    {
        var (decoder, bytes) = Decoder(Shaped);
        decoder.Follow(bytes, 8, 0);
        decoder.IsPinned = true;
        int told = 0;
        decoder.CaretFieldChanged += (_, _) => told++;

        decoder.Follow(bytes, 12, 0);
        Assert.Equal("[2]", decoder.CaretField!.Value.Label);
        Assert.Equal(new ByteRange(12, 1), decoder.HighlightedField);
        Assert.Equal(1, told);

        decoder.Follow(bytes, 12, 0);
        Assert.Equal(1, told);

        decoder.Follow(bytes, 40, 0);
        Assert.Null(decoder.CaretField);
        Assert.Null(decoder.HighlightedField);
    }

    [Fact]
    public void Unpinned_NoFieldFollowsTheCaret()
    {
        var (decoder, bytes) = Decoder(Shaped);
        decoder.Follow(bytes, 9, 0);
        Assert.Null(decoder.CaretField);
        Assert.Null(decoder.HighlightedField);
    }

    [Fact]
    public void ANewTree_ForgetsTheRowsOfTheOld()
    {
        var (decoder, bytes) = Decoder();
        decoder.Hover(decoder.Tree!.Nodes.Child(decoder.Tree.Nodes.Root, 0));
        decoder.Follow(bytes, 0, 16);
        Assert.Null(decoder.HighlightedField);
    }

    [Fact]
    public async Task TheDump_MarksAHoveredReading_ElseAHoveredField()
    {
        using var origin = new MemoryByteOrigin(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray(), "bytes");
        var hex = new HexViewModel();
        await hex.LoadAsync(origin);
        hex.Structs.DefinitionText = Pair;
        hex.Cursor.Place(4);
        hex.Structs.Hover(hex.Structs.Tree!.Nodes.Child(hex.Structs.Tree.Nodes.Root, 1));
        Assert.Equal(new ByteRange(6, 2), hex.HighlightedRange);

        hex.Values.Highlight(hex.Values.Rows[0]);
        Assert.Equal(hex.Values.HighlightedRange, hex.HighlightedRange);
        hex.Dispose();
    }
}
