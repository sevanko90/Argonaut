using Argonaut.Ui.TableGrid;

namespace Argonaut.Tests.Ui.TableGrid;

/// <summary>
/// Verifies the TableStructure width heuristic: driven by the per-column maximum character count
/// its caller measured, clamped to 6..40 characters - plus the relabelling that carries the
/// character counts over untouched. Pixels are the grid's business, not the structure's.
/// </summary>
public class TableStructureTests
{
    private static TableStructure Build(string[] names, params int[] maxChars)
        => TableStructure.FromMaxChars(names, maxChars);

    [Fact]
    public void ShortCountClampsToMinWidth()
    {
        var structure = Build(["abc"], 3);

        Assert.Equal(6, structure.Columns[0].MaxChars);
    }

    [Fact]
    public void VeryLargeCountClampsToMaxWidth()
    {
        var structure = Build([new string('x', 100)], 100);

        Assert.Equal(40, structure.Columns[0].MaxChars);
    }

    [Fact]
    public void CountBetweenTheClampsDrivesWidth()
    {
        // 19 characters, inside the 6..40 clamp, so the count is used as measured.
        var structure = Build(["id"], 19);

        Assert.Equal(19, structure.Columns[0].MaxChars);
    }

    [Fact]
    public void EachColumnWidthIsIndependent()
    {
        var structure = Build(["a", "much-longer-header"], 1, 18);

        Assert.Equal(6, structure.Columns[0].MaxChars);
        Assert.Equal(18, structure.Columns[1].MaxChars);
    }

    [Fact]
    public void ColumnWithNoMeasuredCount_FallsBackToItsNameLength()
    {
        // The counts span is shorter than the names list - column 1 was never measured.
        var structure = Build(["a", "a-long-column-header"], 1);

        Assert.Equal("a-long-column-header".Length, structure.Columns[1].MaxChars);
    }

    [Fact]
    public void ColumnCount_MatchesNameCount()
    {
        var structure = Build(["a", "b", "c"], 1, 1, 1);

        Assert.Equal(3, structure.ColumnCount);
    }

    [Fact]
    public void WithNames_KeepsWidthsAndReplacesLabels()
    {
        var structure = Build(["id", "description"], 2, 30);

        var renamed = structure.WithNames(["Column 1", "Column 2"]);

        Assert.Equal("Column 1", renamed.Columns[0].Name);
        Assert.Equal("Column 2", renamed.Columns[1].Name);
        Assert.Equal(structure.Columns[0].MaxChars, renamed.Columns[0].MaxChars);
        Assert.Equal(structure.Columns[1].MaxChars, renamed.Columns[1].MaxChars);
    }

    [Fact]
    public void WithNames_FewerNamesThanColumns_LeavesTheRestBlank()
    {
        var structure = Build(["a", "b"], 1, 1);

        var renamed = structure.WithNames(["only"]);

        Assert.Equal("only", renamed.Columns[0].Name);
        Assert.Equal(string.Empty, renamed.Columns[1].Name);
        Assert.Equal(structure.Columns[1].MaxChars, renamed.Columns[1].MaxChars);
    }
}
