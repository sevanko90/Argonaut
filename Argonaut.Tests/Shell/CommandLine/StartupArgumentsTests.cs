using Argonaut.Engine.Detection;
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
    public void Reads_the_view_to_force(string name, FileTypeDetector.FileKind kind)
    {
        var parsed = Parse("big.ndjson", "--view", name);

        Assert.Null(parsed.Error);
        Assert.Equal(kind, parsed.View);
    }

    [Fact]
    public void Names_the_views_when_one_is_unknown()
        => Assert.Equal("--view: \"tree\" is not a view. Use one of: json, ndjson, csv, tsv, raw.", Parse("a.json", "--view", "tree").Error);

    [Fact]
    public void Keeps_positional_paths_in_order_around_options()
    {
        var parsed = Parse("--view", "json", "a.json", "b.json");

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
        => Assert.Equal("--lines needs a value.", Parse("a.json", "--lines").Error);

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
}
