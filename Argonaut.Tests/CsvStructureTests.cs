using Argonaut.Features.Csv;

namespace Argonaut.Tests;

/// <summary>
/// Verifies the CsvStructure width heuristic: driven by the per-column maximum character count
/// its caller measured, clamped to [60, 320], with the WidthFor fallback for out-of-range
/// columns - plus the header cells and the relabelling that carries widths over untouched.
/// </summary>
public class CsvStructureTests
{
    private static CsvStructure Build(string[] names, params int[] maxChars)
        => CsvStructure.FromMaxChars(names, maxChars);

    [Fact]
    public void ShortCountClampsToMinWidth()
    {
        var structure = Build(["abc"], 3);

        Assert.Equal(60, structure.Columns[0].Width);
    }

    [Fact]
    public void VeryLargeCountClampsToMaxWidth()
    {
        var structure = Build([new string('x', 100)], 100);

        Assert.Equal(320, structure.Columns[0].Width);
    }

    [Fact]
    public void CountBetweenTheClampsDrivesWidth()
    {
        // 19 chars: 19*7 + 16 = 149, within [60, 320].
        var structure = Build(["id"], 19);

        Assert.Equal(149, structure.Columns[0].Width);
    }

    [Fact]
    public void EachColumnWidthIsIndependent()
    {
        var structure = Build(["a", "much-longer-header"], 1, 18);

        Assert.Equal(60, structure.Columns[0].Width);
        Assert.True(structure.Columns[1].Width > structure.Columns[0].Width);
    }

    [Fact]
    public void ColumnWithNoMeasuredCount_FallsBackToItsNameLength()
    {
        // The counts span is shorter than the names list - column 1 was never measured.
        var structure = Build(["a", "a-long-column-header"], 1);

        Assert.Equal(156, structure.Columns[1].Width); // 20*7 + 16
    }

    [Fact]
    public void ColumnCount_MatchesNameCount()
    {
        var structure = Build(["a", "b", "c"], 1, 1, 1);

        Assert.Equal(3, structure.ColumnCount);
    }

    [Fact]
    public void TotalWidth_IsSumOfColumnWidths()
    {
        var structure = Build(["a", "b"], 1, 1);

        Assert.Equal(structure.Columns[0].Width + structure.Columns[1].Width, structure.TotalWidth);
    }

    [Fact]
    public void HeaderCells_CarryTheNamesAndTheirColumnWidths()
    {
        var structure = Build(["id", "description"], 2, 30);

        Assert.Equal(2, structure.HeaderCells.Count);
        Assert.Equal("id", structure.HeaderCells[0].Text);
        Assert.Equal(structure.Columns[1].Width, structure.HeaderCells[1].Width);
    }

    [Fact]
    public void WidthFor_OutOfRangeColumn_FallsBackToMinWidth()
    {
        var structure = Build(["a"], 1);

        Assert.Equal(60, structure.WidthFor(5));
    }

    [Fact]
    public void WidthFor_NegativeColumn_FallsBackToMinWidth()
    {
        var structure = Build(["a"], 1);

        Assert.Equal(60, structure.WidthFor(-1));
    }

    [Fact]
    public void WithNames_KeepsWidthsAndReplacesLabels()
    {
        var structure = Build(["id", "description"], 2, 30);

        var renamed = structure.WithNames(["Column 1", "Column 2"]);

        Assert.Equal("Column 1", renamed.Columns[0].Name);
        Assert.Equal("Column 2", renamed.HeaderCells[1].Text);
        Assert.Equal(structure.Columns[1].Width, renamed.Columns[1].Width);
        Assert.Equal(structure.TotalWidth, renamed.TotalWidth);
    }

    [Fact]
    public void WithNames_FewerNamesThanColumns_LeavesTheRestBlank()
    {
        var structure = Build(["a", "b"], 1, 1);

        var renamed = structure.WithNames(["only"]);

        Assert.Equal("only", renamed.Columns[0].Name);
        Assert.Equal(string.Empty, renamed.Columns[1].Name);
        Assert.Equal(structure.Columns[1].Width, renamed.Columns[1].Width);
    }
}
