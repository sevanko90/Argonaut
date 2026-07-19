using Argonaut.Features.Csv;

namespace Argonaut.Tests;

/// <summary>
/// Verifies the CsvColumnLayout width heuristic: driven by the longer of header vs sampled
/// content, clamped to [60, 320], and the WidthFor fallback for out-of-range columns.
/// </summary>
public class CsvColumnLayoutTests
{
    [Fact]
    public void ShortHeaderAndNoSampleRows_ClampsToMinWidth()
    {
        var layout = CsvColumnLayout.Compute(["abc"], []);

        Assert.Equal(60, layout.Widths[0]);
    }

    [Fact]
    public void VeryLongHeader_ClampsToMaxWidth()
    {
        var layout = CsvColumnLayout.Compute([new string('x', 100)], []);

        Assert.Equal(320, layout.Widths[0]);
    }

    [Fact]
    public void SampleRowLongerThanHeader_DrivesWidth()
    {
        var layout = CsvColumnLayout.Compute(["id"], [["a-much-longer-value"]]);

        // "a-much-longer-value" is 19 chars: 19*7 + 16 = 149, within [60, 320].
        Assert.Equal(149, layout.Widths[0]);
    }

    [Fact]
    public void HeaderLongerThanAllSampleRows_DrivesWidth()
    {
        var layout = CsvColumnLayout.Compute(["a-long-column-header"], [["short"]]);

        // Header is 20 chars vs sample's 5 chars, so the header wins: 20*7 + 16 = 156.
        Assert.Equal(156, layout.Widths[0]);
    }

    [Fact]
    public void EachColumnWidthIsIndependent()
    {
        var layout = CsvColumnLayout.Compute(["a", "much-longer-header"], [["1", "2"]]);

        Assert.Equal(60, layout.Widths[0]);
        Assert.True(layout.Widths[1] > layout.Widths[0]);
    }

    [Fact]
    public void SampleRowShorterThanColumnCount_DoesNotAffectLaterColumns()
    {
        var layout = CsvColumnLayout.Compute(["a", "b"], [["only-one-field"]]);

        Assert.Equal(60, layout.Widths[1]);
    }

    [Fact]
    public void ColumnCount_MatchesHeaderFieldCount()
    {
        var layout = CsvColumnLayout.Compute(["a", "b", "c"], []);

        Assert.Equal(3, layout.ColumnCount);
    }

    [Fact]
    public void TotalWidth_IsSumOfColumnWidths()
    {
        var layout = CsvColumnLayout.Compute(["a", "b"], []);

        Assert.Equal(layout.Widths[0] + layout.Widths[1], layout.TotalWidth);
    }

    [Fact]
    public void WidthFor_OutOfRangeColumn_FallsBackToMinWidth()
    {
        var layout = CsvColumnLayout.Compute(["a"], []);

        Assert.Equal(60, layout.WidthFor(5));
    }

    [Fact]
    public void WidthFor_NegativeColumn_FallsBackToMinWidth()
    {
        var layout = CsvColumnLayout.Compute(["a"], []);

        Assert.Equal(60, layout.WidthFor(-1));
    }
}
