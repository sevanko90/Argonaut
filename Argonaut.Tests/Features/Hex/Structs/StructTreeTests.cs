using System.Buffers.Binary;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Hex.Structs;
using Argonaut.Features.Hex.Values;
using Argonaut.Ui.Tree;

namespace Argonaut.Tests.Features.Hex.Structs;

public sealed class StructTreeTests
{
    private const string Sample = "struct Sample { int32_t count; float ratio; bool enabled; char name[256]; };";

    private static StructTree Tree(string source, string name, byte[] bytes, long start = 0, long count = 1, ByteOrder order = ByteOrder.Little)
    {
        var definition = StructParser.Parse(source);
        var laid = StructLayoutPass.Lay(definition, name, StructTarget.Lp64);
        Assert.True(laid.Struct is not null, laid.Problem?.Message);
        return new StructTree(new StructNodes(laid.Struct!, count), new MemoryByteSource(bytes), start, order, StructTarget.Lp64);
    }

    /// <summary>The visible rows as the surface draws them: indentation, then the runs' text.</summary>
    private static List<string> Rows(StructTree tree)
    {
        var cursor = tree.NewCursor();
        var rows = new List<string>();
        if (!cursor.MoveToStart())
            return rows;

        do
        {
            rows.Add(Text(tree, cursor.Current));
        }
        while (cursor.MoveNext());
        return rows;
    }

    /// <summary>A row's text with its column padding closed up to one space - what it says, not
    /// how it lines up (<see cref="Columns_LineUpAmongSiblings"/> checks that).</summary>
    private static string Text(StructTree tree, TreeRow row)
        => new string(' ', row.Depth * 2) + System.Text.RegularExpressions.Regex.Replace(RawText(tree, row).TrimEnd(), " {2,}", " ");

    private static string RawText(StructTree tree, TreeRow row)
    {
        var runs = new List<TreeRun>();
        tree.Painter.AppendRuns(row, runs);
        return string.Join(string.Empty, runs.Select(r => r.Text));
    }

    private static byte[] SampleBytes(int count, float ratio, bool enabled, string name)
    {
        var bytes = new byte[268];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, count);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4), ratio);
        bytes[8] = enabled ? (byte)1 : (byte)0;
        System.Text.Encoding.UTF8.GetBytes(name).CopyTo(bytes, 9);
        return bytes;
    }

    [Fact]
    public void TheSample_ReadsAsItsFields_WithItsPadding()
    {
        var tree = Tree(Sample, "Sample", SampleBytes(42, 1.5f, true, "Argonaut"));

        Assert.Equal(
        [
            "count int32_t 42",
            "ratio float 1.5",
            "enabled bool true",
            "name char[256] \"Argonaut\"",
            "padding 3 bytes",
        ], Rows(tree));
    }

    [Fact]
    public void AStructAtAnUnalignedOffset_ReadsFromThere()
    {
        var bytes = new byte[3].Concat(SampleBytes(-7, 0.25f, false, "x")).ToArray();
        var rows = Rows(Tree(Sample, "Sample", bytes, start: 3));

        Assert.Equal("count int32_t -7", rows[0]);
        Assert.Equal("ratio float 0.25", rows[1]);
        Assert.Equal("enabled bool false", rows[2]);
    }

    [Fact]
    public void FieldsPastTheEnd_ShowADash()
    {
        var rows = Rows(Tree("struct A { int a; int b; double c; };", "A", [1, 0, 0, 0, 2, 0]));

        Assert.Equal("a int 1", rows[0]);
        Assert.Equal("b int -", rows[1]);
        Assert.Equal("c double -", rows[2]);
    }

    [Theory]
    [InlineData(ByteOrder.Little, "0x01020304: 67305985")]
    [InlineData(ByteOrder.Big, "0x01020304: 16909060")]
    public void TheByteOrder_DecidesHowAFieldReads(ByteOrder order, string expected)
    {
        var rows = Rows(Tree("struct A { uint32_t value; };", "A", [1, 2, 3, 4], order: order));
        Assert.Equal($"value uint32_t {expected.Split(": ")[1]}", rows[0]);
    }

    [Fact]
    public void ALongSelection_ReadsAsAnArray_ItsElementsClosed_UntilOpened()
    {
        byte[] bytes = [1, 0, 2, 0, 3, 0];
        var tree = Tree("struct P { short v; };", "P", bytes, count: 3);

        Assert.Equal(["[0] P 1 field", "[1] P 1 field", "[2] P 1 field"], Rows(tree));

        var cursor = tree.NewCursor();
        cursor.MoveToStart();
        cursor.MoveNext();
        tree.Toggle(cursor.Current);

        Assert.Equal(["[0] P 1 field", "[1] P 1 field", "  v short 2", "[2] P 1 field"], Rows(tree));
    }

    [Fact]
    public void WalkingBack_VisitsTheSameRows_InReverse()
    {
        var tree = Tree("struct In { char c; short s; }; struct Out { In a; int grid[2][2]; In b[2]; };", "Out", new byte[64]);
        var cursor = tree.NewCursor();
        cursor.MoveToStart();
        while (cursor.MoveNext())
        {
            if (cursor.Current.Shape == TreeRowShape.Open && !cursor.Current.IsExpanded)
                tree.Toggle(cursor.Current);
        }

        var forward = new List<long>();
        cursor.MoveToStart();
        do
            forward.Add(cursor.Current.Start);
        while (cursor.MoveNext());

        var backward = new List<long>();
        cursor.MoveToEnd();
        do
            backward.Add(cursor.Current.Start);
        while (cursor.MovePrevious());
        backward.Reverse();

        Assert.Equal(forward, backward);
        Assert.Equal(forward.Order(), forward);
        Assert.Equal(forward.Distinct().Count(), forward.Count);
    }

    [Fact]
    public void AMillionElementArray_CostsNothing_UntilScrolledTo()
    {
        var tree = Tree("struct P { int v; };", "P", new byte[4], count: 1_000_000);
        var cursor = tree.NewCursor();
        long position = tree.Nodes.Child(tree.Nodes.Root, 500_000).Position;
        Assert.True(cursor.SeekTo(position));
        Assert.Equal("[500000] P 1 field", Text(tree, cursor.Current));
        Assert.True(cursor.MoveNext());
        Assert.Equal("[500001] P 1 field", Text(tree, cursor.Current));
    }

    [Fact]
    public void SeekingAHiddenRow_LandsOnTheClosedRowHidingIt()
    {
        var tree = Tree("struct P { int v[4]; int after; };", "P", new byte[20]);
        var array = tree.Nodes.Child(tree.Nodes.Root, 0);
        var hidden = tree.Nodes.Child(array, 2);

        var cursor = tree.NewCursor();
        cursor.SeekTo(hidden.Position);
        Assert.Equal(array.Position, cursor.Current.Start);
        Assert.True(tree.Hides(cursor.Current, hidden.Position));
    }

    [Fact]
    public void SeekingAnOffset_StandsOnTheDeepestVisibleRowHoldingIt()
    {
        var tree = Tree("struct In { short a; short b; }; struct Out { int x; In in; };", "Out", new byte[8]);
        var cursor = tree.NewCursor();
        tree.SeekScrollPosition(cursor, 6);

        Assert.Equal("  b short 0", Text(tree, cursor.Current));
        Assert.Equal(6, tree.ScrollPosition(cursor.Current));
    }

    [Fact]
    public void Enums_ShowTheirNames_AndFlagsCombine()
    {
        const string source = """
            enum Mode : uint8_t { Off, On, Auto = 7 };
            enum Access { Read = 1, Write = 2, Run = 4 };
            struct A { Mode a; Mode b; Mode c; uint8_t pad; Access d; Access e; };
            """;
        byte[] bytes = [1, 7, 9, 0, 6, 0, 0, 0, 8, 0, 0, 0];
        var rows = Rows(Tree(source, "A", bytes));

        Assert.Equal("a Mode On (1)", rows[0]);
        Assert.Equal("b Mode Auto (7)", rows[1]);
        Assert.Equal("c Mode 9", rows[2]);
        Assert.Equal("d Access Write | Run (6)", rows[4]);
        Assert.Equal("e Access 8", rows[5]);
    }

    [Theory]
    [InlineData(ByteOrder.Little, new byte[] { 0b1110_1101, 0b0000_0001 }, "5", "-3", "1")]
    [InlineData(ByteOrder.Big, new byte[] { 0b1011_1011, 0b1000_0000 }, "5", "-3", "1")]
    public void Bitfields_ReadTheirBits_InTheByteOrder(ByteOrder order, byte[] bytes, string a, string b, string c)
    {
        var rows = Rows(Tree("struct B { unsigned short a : 3; short b : 4; bool c : 1; unsigned short d : 8; };", "B", bytes, order: order));

        Assert.Equal($"a unsigned short : 3 {a}", rows[0]);
        Assert.Equal($"b short : 4 {b}", rows[1]);
        Assert.Equal($"c bool : 1 {(c == "1" ? "true" : "false")}", rows[2]);
    }

    [Fact]
    public void CharacterArrays_ReadAsText_ByTheirWidth()
    {
        var bytes = new byte[16 + 16];
        "hi\n\"x\""u8.ToArray().CopyTo(bytes, 0);
        System.Text.Encoding.Unicode.GetBytes("wide").CopyTo(bytes, 16);
        var rows = Rows(Tree("struct T { char narrow[16]; char16_t wide[8]; };", "T", bytes));

        Assert.Equal("narrow char[16] \"hi\\n\\\"x\\\"\"", rows[0]);
        Assert.Equal("wide char16_t[8] \"wide\"", rows[1]);
    }

    [Fact]
    public void AnonymousMembers_BasesAndUnions_SayWhatTheyAre()
    {
        const string source = """
            struct Base { int id; };
            struct D : Base { union { int i; float f; }; char c; };
            """;
        var rows = Rows(Tree(source, "D", new byte[12]));

        Assert.Equal("(base Base) Base 1 field", rows[0]);
        Assert.Equal("  id int 0", rows[1]);
        Assert.StartsWith("(union) ", rows[2]);
        Assert.EndsWith(" 2 fields", rows[2]);
    }

    [Fact]
    public void Columns_LineUpAmongSiblings()
    {
        var tree = Tree(Sample, "Sample", SampleBytes(42, 1.5f, true, "Argonaut"));
        var cursor = tree.NewCursor();
        var rows = new List<string>();
        for (bool more = cursor.MoveToStart(); more; more = cursor.MoveNext())
            rows.Add(RawText(tree, cursor.Current));

        Assert.Equal(
        [
            "count    int32_t    42",
            "ratio    float      1.5",
            "enabled  bool       true",
            "name     char[256]  \"Argonaut\"",
            "padding             3 bytes",
        ], rows);
    }

    [Fact]
    public void TheRoot_IsNeverARow_SoTheFirstRowCannotGoBack()
    {
        var tree = Tree(Sample, "Sample", new byte[268]);
        var cursor = tree.NewCursor();

        Assert.True(cursor.MoveToStart());
        Assert.Equal(0, cursor.Current.Depth);
        Assert.False(cursor.MovePrevious());
        Assert.True(cursor.SeekTo(0));
        Assert.Equal("count int32_t 0", Text(tree, cursor.Current));
        Assert.Empty(cursor.Ancestors);
    }

    [Fact]
    public void CollapseDeep_ForgetsWhatWasOpenedBeneath()
    {
        var tree = Tree("struct P { int v[2]; };", "P", new byte[8], count: 2);
        var cursor = tree.NewCursor();
        cursor.MoveToStart();
        Assert.True(tree.ExpandDeep(cursor.Current, 100));
        Assert.Equal(5, Rows(tree).Count);

        tree.CollapseDeep(cursor.Current);
        Assert.Equal(2, Rows(tree).Count);
    }
}
