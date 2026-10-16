using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Selection;

namespace Argonaut.Tests.Engine.Selection;

/// <summary>
/// Verifies <see cref="SelectionByteOrigin"/> against a byte-by-byte copy of the selection: the
/// whole document and every range of it read the same through contiguous spans (each cut at a
/// run's end) and through gathering copies - and nothing of a run is ever handed out joined to
/// the next.
/// </summary>
public class SelectionByteOriginTests
{
    private static SelectionByteOrigin Select(byte[] file, SelectionUnit unit, string filter)
    {
        var whole = new MemoryByteOrigin(file, "big.txt");
        var selection = LineSelectionLocator.Locate(whole.Open(), SelectionFilter.Parse(new StringReader(filter), unit));
        return new SelectionByteOrigin(whole, selection);
    }

    /// <summary>The selection copied out by hand from its runs - what every read must agree with.</summary>
    private static byte[] Copy(byte[] file, LineSelection selection)
    {
        var copy = new List<byte>();
        foreach (var run in selection.Runs)
        {
            copy.AddRange(file.AsSpan((int)run.FileOffset, (int)run.Length).ToArray());
            if (run.AddsNewline)
                copy.Add((byte)'\n');
        }

        return copy.ToArray();
    }

    /// <summary>Everything <paramref name="source"/> holds, read span by span as a scan would -
    /// advancing by what each read returned.</summary>
    private static byte[] ReadBySpans(IByteSource source, int maxLength)
    {
        var read = new List<byte>();
        long offset = 0;
        while (true)
        {
            var span = source.GetContiguousSpan(offset, maxLength);
            if (span.IsEmpty)
                break;

            read.AddRange(span.ToArray());
            offset += span.Length;
        }

        return read.ToArray();
    }

    private static byte[] File()
    {
        var builder = new StringBuilder();
        var random = new Random(5);
        for (int line = 1; line <= 2_000; line++)
            builder.Append(line).Append(' ').Append('x', random.Next(0, 40)).Append(line % 7 == 0 ? "\r\n" : "\n");
        builder.Append("last line, no newline");
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    [Theory]
    [InlineData(SelectionUnit.Lines, "1, 3-9\n500-600\n1999-")]
    [InlineData(SelectionUnit.Lines, "2001")]
    [InlineData(SelectionUnit.Bytes, "0-9, 100-4000\n20000-20005, 30000-")]
    public void Reads_the_selection_and_only_the_selection(SelectionUnit unit, string filter)
    {
        byte[] file = File();
        using var origin = Select(file, unit, filter);
        byte[] expected = Copy(file, origin.Selection);
        Assert.Equal(expected.Length, origin.AvailableLength);

        var source = origin.Open();
        try
        {
            Assert.Equal(expected, ReadBySpans(source, 1 << 20));
            Assert.Equal(expected, ReadBySpans(source, 7));

            var gathered = new byte[expected.Length + 10];
            Assert.Equal(expected.Length, source.CopyTo(0, gathered));
            Assert.Equal(expected, gathered[..expected.Length]);
        }
        finally
        {
            source.Release();
        }
    }

    [Fact]
    public void A_span_never_runs_on_from_one_run_into_the_next()
    {
        byte[] file = File();
        using var origin = Select(file, SelectionUnit.Lines, "1-2, 10-11, 2001");
        var source = origin.Open();
        try
        {
            // Lines 1-2 are one run; a read from its start stops at its end, before line 10.
            var first = source.GetContiguousSpan(0, 1 << 20);
            Assert.Equal(origin.Selection.Runs[0].Length, first.Length);
            Assert.Equal((byte)'\n', first[^1]);
        }
        finally
        {
            source.Release();
        }
    }

    [Fact]
    public void Every_range_of_it_reads_as_the_same_bytes_of_the_whole()
    {
        byte[] file = File();
        using var origin = Select(file, SelectionUnit.Lines, "5-6, 40, 41, 100-102, 1999-");
        byte[] expected = Copy(file, origin.Selection);
        var random = new Random(3);

        for (int trial = 0; trial < 300; trial++)
        {
            int start = random.Next(0, expected.Length);
            int length = random.Next(0, expected.Length - start + 1);

            var range = origin.OpenRange(start, length);
            try
            {
                Assert.Equal(length, range.AvailableLength);
                Assert.Equal(expected.AsSpan(start, length).ToArray(), ReadBySpans(range, 1 << 20));
            }
            finally
            {
                range.Release();
            }
        }
    }

    [Fact]
    public void A_range_past_the_end_of_the_selection_is_refused()
    {
        using var origin = Select(File(), SelectionUnit.Lines, "1-3");
        Assert.Throws<ArgumentOutOfRangeException>(() => origin.OpenRange(1, origin.AvailableLength));
    }

    [Fact]
    public void Says_what_is_selected()
    {
        byte[] file = File();
        using var lines = Select(file, SelectionUnit.Lines, "1-3, 10");
        using var bytes = Select(file, SelectionUnit.Bytes, "0-9, 100-200");

        Assert.Equal("big.txt (4 lines selected)", lines.DisplayName);
        Assert.Equal("2 ranges selected", bytes.Description);
        Assert.Null(lines.Path);
    }

    [Fact]
    public void An_empty_selection_is_an_empty_document()
    {
        using var origin = Select(File(), SelectionUnit.Lines, "9000-");
        var source = origin.Open();
        try
        {
            Assert.Equal(0, source.AvailableLength);
            Assert.True(source.GetContiguousSpan(0, 10).IsEmpty);
        }
        finally
        {
            source.Release();
        }
    }

    [Fact]
    public void Numbers_each_line_as_the_file_does()
    {
        byte[] file = Encoding.UTF8.GetBytes("a\nbb\nccc\ndddd\neeeee\n");
        using var origin = Select(file, SelectionUnit.Lines, "2, 4-5");
        var numbering = origin.Numbering;

        // View: "bb\ndddd\neeeee\n" - lines 2, 4, 5 of the file.
        Assert.Equal([2L, 4L, 5L], new long[] { numbering.Line(1), numbering.Line(2), numbering.Line(3) });
        Assert.Equal(2, numbering.Offset(0));
        Assert.Equal(9, numbering.Offset(3));
        Assert.False(numbering.LabelsLineStarts);
        Assert.Same(numbering, origin.Numbering);
    }

    [Fact]
    public void Labels_a_byte_selections_lines_by_where_they_start_in_the_file()
    {
        byte[] file = Encoding.UTF8.GetBytes("0123456789\nabcdefghij\n");
        using var origin = Select(file, SelectionUnit.Bytes, "2-4, 11-21");
        var numbering = origin.Numbering;

        // View: "234\n" (a newline added) then "abcdefghij\n".
        Assert.True(numbering.LabelsLineStarts);
        Assert.Equal("2", numbering.GutterLabel(1, 0));
        Assert.Equal("11", numbering.GutterLabel(2, 4));
        Assert.Equal(2, numbering.Line(2));
    }

    [Fact]
    public void Maps_a_range_of_the_selection_back_to_the_file()
    {
        byte[] file = Encoding.UTF8.GetBytes("a\nbb\nccc\ndddd\n");
        using var origin = Select(file, SelectionUnit.Lines, "2, 4");

        // View: "bb\ndddd\n". Inside one run a range maps whole; across the seam only its start.
        Assert.Equal(new ByteRange(9, 4), origin.FileRangeOf(new ByteRange(3, 4)));
        Assert.Equal(ByteRange.At(3), origin.FileRangeOf(new ByteRange(1, 4)));
        Assert.Equal(ByteRange.At(2), origin.FileRangeOf(ByteRange.At(0)));
    }

    [Fact]
    public void Finds_a_range_of_the_file_in_the_selection_or_where_it_picks_up_after()
    {
        byte[] file = Encoding.UTF8.GetBytes("a\nbb\nccc\ndddd\n");
        using var origin = Select(file, SelectionUnit.Lines, "2, 4");

        // View: "bb\ndddd\n" - file lines 2 (offset 2) and 4 (offset 9).
        Assert.Equal(new ByteRange(3, 4), origin.ViewRangeOf(new ByteRange(9, 4)));
        Assert.Equal(ByteRange.At(3), origin.ViewRangeOf(new ByteRange(5, 3)));
        Assert.Equal(ByteRange.At(0), origin.ViewRangeOf(ByteRange.At(0)));
        Assert.Equal(ByteRange.At(7), origin.ViewRangeOf(ByteRange.At(14)));
    }
}
