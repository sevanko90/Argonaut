using Argonaut.Engine.Detection;
using Argonaut.Engine.Selection;
using Argonaut.Shell.CommandLine;

namespace Argonaut.Tests.Shell.CommandLine;

public class StartupArgumentsTests
{
    private static StartupArguments Parse(params string[] args) => StartupArguments.Parse(args, windowsSwitches: false);

    [Theory]
    [InlineData("1000000-2000000", 1_000_000L, 2_000_000L)]
    [InlineData("1m-2m", 1_000_000L, 2_000_000L)]
    [InlineData("2.5m+500k", 2_500_000L, 2_999_999L)]
    [InlineData("5", 5L, 5L)]
    [InlineData("1-1", 1L, 1L)]
    [InlineData("1K-2k", 1_000L, 2_000L)]
    public void Reads_a_bounded_line_range(string text, long first, long last)
    {
        var parsed = Parse("big.ndjson", "--lines", text);

        Assert.Null(parsed.Error);
        Assert.Equal(new LineSpan(first, last), parsed.Lines);
    }

    [Fact]
    public void A_range_with_no_end_runs_to_the_end_of_the_file()
        => Assert.Equal(new LineSpan(1_000_000, null), Parse("big.ndjson", "--lines=1m-").Lines);

    [Theory]
    [InlineData("0-5")]
    [InlineData("5-4")]
    [InlineData("a-b")]
    [InlineData("-5")]
    [InlineData("1.5")]
    [InlineData("1.0000005m")]
    [InlineData("5+0")]
    [InlineData("")]
    public void Rejects_what_is_not_a_line_range(string text)
    {
        var parsed = Parse("big.ndjson", $"--lines={text}");

        Assert.Null(parsed.Lines);
        Assert.Contains("--lines", parsed.Error);
    }

    [Theory]
    [InlineData("raw", FileTypeDetector.FileKind.Unidentified)]
    [InlineData("CSV", FileTypeDetector.FileKind.Csv)]
    [InlineData("ndjson", FileTypeDetector.FileKind.Ndjson)]
    [InlineData("xml", FileTypeDetector.FileKind.Xml)]
    public void Reads_the_view_to_force(string name, FileTypeDetector.FileKind kind)
    {
        var parsed = Parse("big.ndjson", "--view", name);

        Assert.Null(parsed.Error);
        Assert.Equal(kind, parsed.View);
    }

    [Fact]
    public void Names_the_views_when_one_is_unknown()
        => Assert.Equal("--view: \"tree\" is not a view. Use one of: json, ndjson, xml, csv, tsv, raw.", Parse("a.json", "--view", "tree").Error);

    [Fact]
    public void Keeps_positional_paths_in_order_around_options()
    {
        var parsed = Parse("--help", "a.json", "--unknown", "b.json");

        Assert.Equal(["a.json", "b.json"], parsed.Paths);
        Assert.Null(parsed.Error);
    }

    [Fact]
    public void Ignores_options_it_does_not_know()
    {
        var parsed = Parse("-psn_0_12345", "a.json");

        Assert.Equal(["a.json"], parsed.Paths);
        Assert.Null(parsed.Error);
    }

    [Fact]
    public void An_option_missing_its_value_is_an_error()
    {
        Assert.Equal("--view needs a value.", Parse("a.json", "--view").Error);
        Assert.Equal("--filter needs a file.", Parse("a.json", "--lines", "--filter").Error);
    }

    [Theory]
    [InlineData("--lines")]
    [InlineData("--bytes")]
    public void A_range_option_with_neither_a_range_nor_a_filter_is_an_error(string option)
        => Assert.Equal($"{option} needs a range, or --filter with a file of them.", Parse("a.csv", option).Error);

    [Theory]
    [InlineData("--lines", SelectionUnit.Lines)]
    [InlineData("--bytes", SelectionUnit.Bytes)]
    public void Reads_a_filter_file_for_lines_or_bytes(string option, SelectionUnit unit)
    {
        var parsed = Parse("big.ndjson", option, "--filter", "picks.txt");

        Assert.Null(parsed.Error);
        Assert.Equal("picks.txt", parsed.FilterPath);
        Assert.Equal(unit, parsed.FilterUnit);
        Assert.Null(parsed.Lines);
        Assert.Null(parsed.Bytes);
        Assert.Equal(["big.ndjson"], parsed.Paths);
    }

    [Fact]
    public void A_filter_may_come_before_the_option_it_belongs_to()
    {
        var parsed = Parse("--filter=picks.txt", "big.ndjson", "--lines", "--view", "raw");

        Assert.Null(parsed.Error);
        Assert.Equal(SelectionUnit.Lines, parsed.FilterUnit);
        Assert.Equal(FileTypeDetector.FileKind.Unidentified, parsed.View);
    }

    [Fact]
    public void A_filter_needs_lines_or_bytes_to_say_what_it_lists()
        => Assert.Equal("--filter needs --lines or --bytes, to say whether the file lists lines or bytes.",
            Parse("a.csv", "--filter", "picks.txt").Error);

    [Fact]
    public void A_range_and_a_filter_together_are_an_error()
        => Assert.Equal("Give --lines a range or --filter, not both.", Parse("a.csv", "--lines", "1-5", "--filter", "picks.txt").Error);

    [Fact]
    public void A_filter_opens_one_file_not_a_comparison()
        => Assert.Equal("--bytes opens one file, not a comparison.", Parse("a.csv", "b.csv", "--bytes", "--filter", "picks.txt").Error);

    [Fact]
    public void A_filter_cannot_be_shown_as_xml()
        => Assert.Contains("--view xml cannot show a selection", Parse("a.xml", "--lines", "--filter", "p.txt", "--view", "xml").Error);

    [Fact]
    public void A_filter_cannot_be_shown_as_json()
        => Assert.Contains("--view json cannot show a selection", Parse("a.ndjson", "--lines", "--filter", "p.txt", "--view", "json").Error);

    [Fact]
    public void Lines_open_one_file_not_a_comparison()
        => Assert.NotNull(Parse("a.json", "b.json", "--lines", "1-2").Error);

    [Fact]
    public void Lines_and_view_need_a_file()
        => Assert.NotNull(Parse("--view", "raw").Error);

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Asks_for_help(string arg) => Assert.True(Parse(arg).ShowHelp);

    [Fact]
    public void A_slash_is_a_switch_on_windows_and_a_path_elsewhere()
    {
        Assert.True(StartupArguments.Parse(["/?"], windowsSwitches: true).ShowHelp);

        var elsewhere = StartupArguments.Parse(["/?"], windowsSwitches: false);
        Assert.False(elsewhere.ShowHelp);
        Assert.Equal(["/?"], elsewhere.Paths);
    }

    [Fact]
    public void The_usage_text_lists_every_view()
    {
        foreach (var (name, _) in StartupArguments.ViewNames)
            Assert.Contains(name, CommandLineUsage.Text);
    }

    [Theory]
    [InlineData("0-499", 0L, 499L)]
    [InlineData("1.5g+100m", 1_500_000_000L, 1_599_999_999L)]
    [InlineData("0x10-0x1F", 16L, 31L)]
    [InlineData("100k+1", 100_000L, 100_000L)]
    public void Reads_a_bounded_byte_range(string text, long first, long last)
    {
        var parsed = Parse("big.json", "--bytes", text);

        Assert.Null(parsed.Error);
        Assert.Equal(new ByteSpan(first, last), parsed.Bytes);
    }

    [Fact]
    public void A_byte_range_with_no_end_runs_to_the_end_of_the_file()
        => Assert.Equal(new ByteSpan(0, null), Parse("big.json", "--bytes=0-").Bytes);

    [Theory]
    [InlineData("5")]
    [InlineData("9-3")]
    [InlineData("0xZZ-0x20")]
    [InlineData("1.5-2")]
    public void Rejects_what_is_not_a_byte_range(string text)
    {
        var parsed = Parse("big.json", $"--bytes={text}");

        Assert.Null(parsed.Bytes);
        Assert.Contains("--bytes", parsed.Error);
    }

    [Fact]
    public void Lines_and_bytes_together_are_an_error()
        => Assert.Equal("Use --lines or --bytes, not both.", Parse("a.json", "--lines", "1-2", "--bytes", "0-9").Error);

    [Fact]
    public void Bytes_open_one_file_not_a_comparison()
        => Assert.Equal("--bytes opens one file, not a comparison.", Parse("a.json", "b.json", "--bytes", "0-9").Error);

    [Fact]
    public void View_opens_one_file_not_a_comparison()
        => Assert.Equal("--view opens one file, not a comparison.", Parse("a.json", "b.json", "--view", "raw").Error);

    [Theory]
    [InlineData("1g+0x7FFFFFFFFFFFFFFF")]
    [InlineData("2+0x7FFFFFFFFFFFFFFF")]
    public void A_count_that_runs_past_the_largest_offset_is_an_error(string range)
    {
        var parsed = Parse("a.json", "--bytes", range);

        Assert.Null(parsed.Bytes);
        Assert.Contains("is not a byte range", parsed.Error);
    }

    [Fact]
    public void A_count_that_ends_exactly_at_the_largest_offset_is_a_range()
        => Assert.Equal(long.MaxValue, Parse("a.json", "--bytes", "1+0x7FFFFFFFFFFFFFFF").Bytes!.Value.Last);
}
