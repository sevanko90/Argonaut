using System.Globalization;
using System.Text;
using Argonaut.Engine.Indexing.Lines;

namespace Argonaut.Tests.Engine.Indexing.Lines;

/// <summary>
/// Verifies <see cref="LineNumbering"/> against a document built by hand from chosen lines of a
/// file: every view line is numbered as the file line it came from, and every view byte as the
/// file byte it is a copy of - across runs, at their edges, and for a single excerpt.
/// </summary>
public class LineNumberingTests
{
    [Fact]
    public void From_start_numbers_as_the_document_does()
    {
        Assert.Equal(1, LineNumbering.FromStart.Line(1));
        Assert.Equal(42, LineNumbering.FromStart.Line(42));
        Assert.Equal(0, LineNumbering.FromStart.Offset(0));
        Assert.Equal(1234, LineNumbering.FromStart.Offset(1234));
    }

    [Fact]
    public void An_excerpt_numbers_from_where_it_starts_in_the_file()
    {
        var numbering = LineNumbering.Starting(100, 5_000);

        Assert.Equal(100, numbering.Line(1));
        Assert.Equal(500, numbering.Line(401));
        Assert.Equal(5_000, numbering.Offset(0));
        Assert.Equal(5_010, numbering.Offset(10));
    }

    [Theory]
    [InlineData(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 })]
    [InlineData(new[] { 3 })]
    [InlineData(new[] { 1, 10 })]
    [InlineData(new[] { 2, 3, 4, 7, 9, 10 })]
    [InlineData(new[] { 1, 3, 5, 7, 9 })]
    public void Each_view_line_and_byte_is_numbered_as_the_file_one_it_came_from(int[] selected)
    {
        // Lines of different lengths, so a wrong run is off by more than a constant.
        var file = new StringBuilder();
        var lineStarts = new List<int>();
        for (int line = 1; line <= 10; line++)
        {
            lineStarts.Add(file.Length);
            file.Append(new string((char)('a' + line - 1), line)).Append('\n');
        }

        lineStarts.Add(file.Length);
        string fileText = file.ToString();

        var view = new StringBuilder();
        var viewLineToFileLine = new List<long>();
        var viewByteToFileByte = new List<long>();
        var runs = new List<LineRun>();
        int previous = int.MinValue;
        foreach (int line in selected)
        {
            if (line != previous + 1)
                runs.Add(new LineRun(viewLineToFileLine.Count + 1, line, view.Length, lineStarts[line - 1]));

            viewLineToFileLine.Add(line);
            for (int b = lineStarts[line - 1]; b < lineStarts[line]; b++)
                viewByteToFileByte.Add(b);

            view.Append(fileText, lineStarts[line - 1], lineStarts[line] - lineStarts[line - 1]);
            previous = line;
        }

        var numbering = LineNumbering.FromRuns(runs.ToArray());

        Assert.Equal(runs.Count, numbering.Runs.Length);
        for (int viewLine = 1; viewLine <= viewLineToFileLine.Count; viewLine++)
            Assert.Equal(viewLineToFileLine[viewLine - 1], numbering.Line(viewLine));

        string viewText = view.ToString();
        for (int offset = 0; offset < viewText.Length; offset++)
        {
            long fileOffset = numbering.Offset(offset);
            Assert.Equal(viewByteToFileByte[offset], fileOffset);
            Assert.Equal(fileText[(int)fileOffset], viewText[offset]);
        }
    }

    [Fact]
    public void Past_the_last_run_follows_it_on()
    {
        var numbering = LineNumbering.FromRuns([new LineRun(1, 1, 0, 0), new LineRun(3, 10, 20, 200)]);

        Assert.Equal(13, numbering.Line(6));
        Assert.Equal(230, numbering.Offset(50));
    }

    [Fact]
    public void Runs_must_start_at_the_top_and_move_forwards()
    {
        Assert.Throws<ArgumentException>(() => LineNumbering.FromRuns([]));
        Assert.Throws<ArgumentException>(() => LineNumbering.FromRuns([new LineRun(2, 1, 0, 0)]));
        Assert.Throws<ArgumentException>(() => LineNumbering.FromRuns([new LineRun(1, 1, 5, 0)]));
        Assert.Throws<ArgumentException>(() => LineNumbering.FromRuns([new LineRun(1, 1, 0, 0), new LineRun(1, 5, 10, 50)]));
        Assert.Throws<ArgumentException>(() => LineNumbering.FromRuns([new LineRun(1, 1, 0, 0), new LineRun(3, 5, 0, 50)]));
    }

    [Fact]
    public void Two_numberings_of_the_same_runs_are_equal()
    {
        Assert.Equal(LineNumbering.Starting(100, 5_000), LineNumbering.Starting(100, 5_000));
        Assert.Equal(LineNumbering.FromStart, LineNumbering.Starting(1, 0));
        Assert.NotEqual(LineNumbering.FromStart, LineNumbering.Starting(100, 5_000));
        Assert.NotEqual(
            LineNumbering.Starting(1, 0),
            LineNumbering.FromRuns([new LineRun(1, 1, 0, 0), new LineRun(3, 10, 20, 200)]));
    }

    [Fact]
    public void Labelled_by_line_starts_the_gutter_shows_file_offsets_grouped_the_readers_way()
    {
        var numbering = LineNumbering.ByLineStarts(4_000_000_000, 1_000);

        WithCulture("en-GB", () =>
        {
            Assert.Equal("4,000,000,000", numbering.GutterLabel(1, 0));
            Assert.Equal("4,000,000,512", numbering.GutterLabel(7, 512));
            Assert.Equal("4,000,000,999", numbering.WidestLineStartLabel);
        });
        WithCulture("de-DE", () => Assert.Equal("4.000.000.512", numbering.GutterLabel(7, 512)));
    }

    [Fact]
    public void Labelled_by_number_the_gutter_shows_line_numbers_ungrouped()
    {
        WithCulture("en-GB", () =>
        {
            Assert.Equal("12345", LineNumbering.Starting(12_340, 0).GutterLabel(6, 999));
            Assert.Null(LineNumbering.Starting(12_340, 0).WidestLineStartLabel);
        });
    }

    [Fact]
    public void Labelling_by_line_starts_is_part_of_what_a_numbering_is()
    {
        Assert.NotEqual(LineNumbering.Starting(1, 5), LineNumbering.ByLineStarts(5, 4));
        Assert.NotEqual(LineNumbering.ByLineStarts(5, 4), LineNumbering.ByLineStarts(5, 40));
        Assert.Equal(LineNumbering.ByLineStarts(5, 4), LineNumbering.ByLineStarts(5, 4));
    }

    [Fact]
    public void Labelled_by_line_starts_across_runs_each_line_shows_its_file_offset()
    {
        var numbering = LineNumbering.ByLineStarts([new LineRun(0, 0, 0, 1_000), new LineRun(0, 0, 50, 9_000)], 80);

        WithCulture("en-GB", () =>
        {
            Assert.Equal("1,010", numbering.GutterLabel(2, 10));
            Assert.Equal("9,005", numbering.GutterLabel(7, 55));
            Assert.Equal("9,029", numbering.WidestLineStartLabel);
        });
        Assert.Equal(7, numbering.Line(7));
    }

    private static void WithCulture(string name, Action check)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        try
        {
            check();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
