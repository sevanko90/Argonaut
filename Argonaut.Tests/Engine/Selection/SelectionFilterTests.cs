using Argonaut.Engine.Selection;

namespace Argonaut.Tests.Engine.Selection;

/// <summary>
/// Verifies <see cref="SelectionFilter"/>: entries in every separator and form the command line
/// takes, comments and blank lines skipped, the result in file order with overlaps, touching
/// entries and duplicates merged, and an unreadable entry named by its filter file line.
/// </summary>
public class SelectionFilterTests
{
    private static SelectionRange[] Ranges(string text, SelectionUnit unit = SelectionUnit.Lines)
        => SelectionFilter.Parse(new StringReader(text), unit).Ranges.ToArray();

    private const long End = long.MaxValue;

    [Fact]
    public void Reads_entries_separated_by_commas_newlines_or_both()
    {
        Assert.Equal(
            [new(2, 2), new(5, 9), new(20, 20), new(40, 40)],
            Ranges("2\n5-9, 20\r\n\n40,"));
    }

    [Fact]
    public void Reads_every_form_a_range_takes()
    {
        Assert.Equal(
            [new(10, 14), new(1_000, 1_000), new(2_500_000, 2_500_099), new(3_000_000, End)],
            Ranges("1k\n10+5\n2.5m+100\n3m-"));
    }

    [Fact]
    public void Puts_entries_in_file_order_and_merges_overlaps_touches_and_duplicates()
    {
        Assert.Equal(
            [new(2, 60), new(100, 100), new(5_000, 5_000)],
            Ranges("5000\n40-60\n2-50\n100\n5000\n51"));
    }

    [Fact]
    public void An_entry_to_the_end_swallows_everything_after_it()
        => Assert.Equal([new(1, 3), new(9, End)], Ranges("9-\n20-30\n1-3\n100"));

    [Fact]
    public void Skips_comments_and_blank_lines()
    {
        Assert.Equal(
            [new(1, 1), new(7, 8)],
            Ranges("# picked by the importer\n\n1 # the header\n   \n7-8,# trailing\n#9"));
    }

    [Fact]
    public void Counts_the_entries_it_read_before_merging()
        => Assert.Equal(4, SelectionFilter.Parse(new StringReader("1,2\n2\n# 3\n10"), SelectionUnit.Lines).EntryCount);

    [Fact]
    public void An_empty_filter_selects_nothing()
        => Assert.Empty(Ranges("# nothing yet\n\n"));

    [Fact]
    public void Byte_entries_count_from_zero_and_read_hex_and_g()
    {
        Assert.Equal(
            [new(0, 4_095), new(0x1000_0000, 0x1000_FFFF), new(2_000_000_000, 2_000_065_535)],
            Ranges("0-4095\n0x10000000-0x1000FFFF\n2g-2000065535", SelectionUnit.Bytes));
    }

    [Fact]
    public void A_single_byte_is_an_entry_in_a_filter()
        => Assert.Equal([new(1_000_000, 1_000_000)], Ranges("1m", SelectionUnit.Bytes));

    [Theory]
    [InlineData("1-5\n2-x\n", 2, "\"2-x\" is not a line range.")]
    [InlineData("0\n", 1, "\"0\" is not a line range.")]
    [InlineData("# fine\n\n3,,9-4", 3, "\"9-4\" is not a line range.")]
    public void An_unreadable_entry_fails_the_filter_naming_its_line(string text, int line, string message)
    {
        var error = Assert.Throws<SelectionFilterException>(() => Ranges(text));

        Assert.Equal(line, error.FilterLine);
        Assert.Equal($"Line {line}: {message}", error.Message);
    }

    [Fact]
    public void A_byte_filter_names_its_unit_in_an_error()
        => Assert.Equal("Line 1: \"1-z\" is not a byte range.",
            Assert.Throws<SelectionFilterException>(() => Ranges("1-z", SelectionUnit.Bytes)).Message);
}
