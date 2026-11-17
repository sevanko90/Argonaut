using Argonaut.Engine.Bytes;
using Argonaut.Features.Hex;
using Argonaut.Features.Hex.Values;

namespace Argonaut.Tests.Features.Hex.Values;

/// <summary>The Values tab reads the caret's group as the types its size can hold, in the byte
/// order chosen, and says <c>-</c> for a group cut short by the end.</summary>
public sealed class HexValuesViewModelTests
{
    private static HexValueRow Row(HexValuesViewModel values, string name) => values.Rows.Single(r => r.Name == name);

    private static readonly byte[] Counting = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];

    [Fact]
    public void AGroup_ReadsAsTheTypesOfItsSize()
    {
        var values = new HexValuesViewModel();

        values.Show(0x10, Counting, 1, 8);
        Assert.Equal(["uint8", "int8", "ASCII", "UTF-8"], values.Rows.Select(r => r.Name));
        Assert.Equal("8-bit", values.SizeText);
        Assert.Equal("SOH", Row(values, "ASCII").Value);
        Assert.Equal("01", values.BytesText);
        Assert.Equal(["0000 0001"], values.BitsLines);

        values.Show(0x10, Counting, 2, 8);
        Assert.Equal(["uint16", "int16", "half", "UTF-8"], values.Rows.Select(r => r.Name));
        Assert.Equal("513", Row(values, "uint16").Value);
        Assert.Equal("01 02", values.BytesText);

        values.Show(0x10, Counting, 4, 8);
        Assert.Equal(["uint32", "int32", "float", "Unix s", "UTF-8"], values.Rows.Select(r => r.Name));
        Assert.Equal("67305985", Row(values, "uint32").Value);
        Assert.Equal(["0000 0001  0000 0010  0000 0011  0000 0100"], values.BitsLines);

        values.Show(0x10, Counting, 8, 8);
        Assert.Equal(["uint64", "int64", "double", "Unix s", "Unix ms", "UTF-8"], values.Rows.Select(r => r.Name));
        Assert.Equal("578437695752307201", Row(values, "uint64").Value);
        Assert.Equal("64-bit", values.SizeText);
        Assert.Equal(2, values.BitsLines.Count);
    }

    [Fact]
    public void SwitchingTheOrder_RereadsTheGroup_AndOneOrderIsAlwaysInUse()
    {
        var chosen = new List<ByteOrder>();
        var values = new HexValuesViewModel(ByteOrder.Little, chosen.Add);
        values.Show(0, Counting, 4, 8);

        values.IsBigEndian = true;
        Assert.Equal("16909060", Row(values, "uint32").Value);
        Assert.False(values.IsLittleEndian);
        Assert.Equal([ByteOrder.Big], chosen);

        values.IsBigEndian = false;
        Assert.Equal(ByteOrder.Big, values.Order);
    }

    [Fact]
    public void TheReset_GoesBackToThisMachinesOrder()
    {
        var other = HexValuesViewModel.SystemOrder == ByteOrder.Little ? ByteOrder.Big : ByteOrder.Little;
        var values = new HexValuesViewModel(other);
        Assert.False(values.IsSystemOrder);

        values.UseSystemOrder();

        Assert.Equal(HexValuesViewModel.SystemOrder, values.Order);
        Assert.True(values.IsSystemOrder);
        Assert.Contains(HexValuesViewModel.SystemOrder == ByteOrder.Little ? "little-endian" : "big-endian", HexValuesViewModel.SystemOrderText);
    }

    [Fact]
    public void AShortLastGroup_ReadsAsADash_ButALoneByteIsAlwaysWhole()
    {
        var values = new HexValuesViewModel();
        values.Show(10, [0xFF, 0xFF], 4, 8);
        Assert.All(values.Rows, r => Assert.Equal("-", r.Value));
        Assert.Equal("FF FF", values.BytesText);

        values.Show(10, [0xC3], 1, 8);
        Assert.Equal("195", Row(values, "uint8").Value);
        Assert.Equal("-", Row(values, "UTF-8").Value);
    }

    [Fact]
    public void AUtf8Character_IsReadToItsEnd_PastAOneByteGroup()
    {
        var values = new HexValuesViewModel();
        values.Show(0, [0xC3, 0xA9, 0x41], 1, 8);

        Assert.Equal("é U+00E9", Row(values, "UTF-8").Value);
        values.Highlight(Row(values, "UTF-8"));
        Assert.Equal(new ByteRange(0, 3), values.HighlightedRange);
        values.Highlight(Row(values, "uint8"));
        Assert.Equal(new ByteRange(0, 1), values.HighlightedRange);
    }

    [Fact]
    public void AnyWidth_ReadsAsIntegersOfThatWidth_AndAsText()
    {
        var values = new HexValuesViewModel(ByteOrder.Little);
        values.Show(0, [0xE2, 0x82, 0xAC, 0x00], 3, 8);

        Assert.Equal(["uint24", "int24", "UTF-8"], values.Rows.Select(r => r.Name));
        Assert.Equal("24-bit", values.SizeText);
        Assert.Equal("11305698", Row(values, "uint24").Value);      // 0xAC82E2
        Assert.Equal("-5471518", Row(values, "int24").Value);       // its top bit set
        Assert.Equal("€", Row(values, "UTF-8").Value);
        Assert.Equal("E2 82 AC", values.BytesText);
    }

    [Fact]
    public void TheWidthSteps_SelectFromWhereTheReadingStarts_OneToEight()
    {
        var selected = new List<(long, int)>();
        var values = new HexValuesViewModel(ByteOrder.Little, null, (start, length) => selected.Add((start, length)));
        values.Show(20, Counting, 1, 8);

        Assert.False(values.CanNarrow);
        values.Narrow();
        values.Widen();
        Assert.Equal([(20L, 2)], selected);

        values.Show(20, Counting, 8, 8);
        Assert.False(values.CanWiden);
        values.Narrow();
        Assert.Equal((20L, 7), selected[^1]);
    }

    [Fact]
    public async Task ASelectionOfUpToEightBytes_IsWhatIsRead_AndTheStepsSelect()
    {
        using var origin = new MemoryByteOrigin(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray(), "bytes");
        using var hex = new HexViewModel();
        await hex.LoadAsync(origin);
        hex.GroupSize = 4;

        hex.Cursor.Select(new HexCaret(5, 8));
        Assert.Equal("05 06 07", hex.Values.BytesText);
        Assert.Equal("24-bit", hex.Values.SizeText);

        hex.Values.Widen();
        Assert.Equal(new HexCaret(5, 9), hex.Cursor.Caret);
        Assert.Equal("05 06 07 08", hex.Values.BytesText);

        // Longer than eight: back to the group the selection starts in.
        hex.Cursor.Select(new HexCaret(5, 30));
        Assert.Equal("04 05 06 07", hex.Values.BytesText);
    }

    [Fact]
    public async Task ThePanel_ReadsTheCaretsGroup_AndFollowsTheGrouping()
    {
        using var origin = new MemoryByteOrigin(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray(), "bytes");
        using var hex = new HexViewModel();
        await hex.LoadAsync(origin);

        hex.Cursor.Place(13);
        Assert.Equal("0D", hex.Values.BytesText);

        hex.GroupSize = 4;
        Assert.Equal("0C 0D 0E 0F", hex.Values.BytesText);
        Assert.Equal("0x0000000C", hex.Values.OffsetText);

        // A selection longer than eight reads the group it starts in, whichever way it was taken.
        hex.Cursor.Select(new HexCaret(40, 30));
        Assert.Equal("1C 1D 1E 1F", hex.Values.BytesText);

        hex.GroupSize = 8;
        hex.Cursor.Place(61);
        Assert.Equal("38 39 3A 3B 3C 3D 3E 3F", hex.Values.BytesText);
    }

    [Fact]
    public async Task TheSidePanel_IsRemembered()
    {
        using var origin = new MemoryByteOrigin([1, 2, 3], "bytes");
        var settings = new HexViewSettings();
        using var hex = new HexViewModel(null, settings);
        await hex.LoadAsync(origin);
        var toolbar = Assert.IsType<HexToolbarViewModel>(hex.Toolbar);

        Assert.True(toolbar.ShowsSidePanel);
        toolbar.ShowsSidePanel = false;
        Assert.False(hex.ShowsSidePanel);
        Assert.False(settings.ShowsSidePanel);
    }

    [Fact]
    public async Task TheByteOrder_StartsAtThisMachines_AndIsRemembered()
    {
        using var origin = new MemoryByteOrigin([1, 2, 3, 4], "bytes");
        var settings = new HexViewSettings();
        Assert.Equal(BitConverter.IsLittleEndian ? ByteOrder.Little : ByteOrder.Big, settings.ValuesByteOrder);

        using var hex = new HexViewModel(null, settings);
        await hex.LoadAsync(origin);
        hex.Values.Order = ByteOrder.Big;
        Assert.Equal(ByteOrder.Big, settings.ValuesByteOrder);

        using var again = new HexViewModel(null, settings);
        await again.LoadAsync(origin);
        Assert.Equal(ByteOrder.Big, again.Values.Order);
    }

    [Fact]
    public void ARememberedOrder_ReadsBackByName()
    {
        var settings = new HexViewSettings { ValuesByteOrder = ByteOrder.Big };
        string json = System.Text.Json.JsonSerializer.Serialize(settings, HexViewSettings.JsonTypeInfo);
        Assert.Contains("\"Big\"", json);
        Assert.Equal(ByteOrder.Big, System.Text.Json.JsonSerializer.Deserialize(json, HexViewSettings.JsonTypeInfo)!.ValuesByteOrder);
    }
}
