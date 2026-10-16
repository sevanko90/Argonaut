using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Selection;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Engine.Selection;

/// <summary>
/// Verifies <see cref="LineSelectionLocator"/> against a naive split on <c>\n</c>: the view its
/// runs make is exactly the selected lines (or bytes) end to end, every run ends a line, and the
/// line numbers and counts are the file's - over a whole source, one served in small pieces, and
/// one still arriving.
/// </summary>
public class LineSelectionLocatorTests
{
    private const long End = long.MaxValue;

    private static LineSelection Locate(IByteSource source, SelectionUnit unit, params (long First, long Last)[] ranges)
        => Locate(source, unit, checkpoints: null, ranges);

    private static LineSelection Locate(IByteSource source, SelectionUnit unit, LineCheckpoints? checkpoints, params (long First, long Last)[] ranges)
    {
        string text = string.Join("\n", ranges.Select(r => r.Last == End ? $"{r.First}-" : $"{r.First}-{r.Last}"));
        return LineSelectionLocator.Locate(source, SelectionFilter.Parse(new StringReader(text), unit), checkpoints: checkpoints);
    }

    private static LineSelection Locate(string text, SelectionUnit unit, params (long First, long Last)[] ranges)
        => Locate(new MemoryByteSource(Encoding.UTF8.GetBytes(text)), unit, ranges);

    /// <summary>What a view over <paramref name="selection"/> reads: each run's bytes and the
    /// newline it adds.</summary>
    private static string View(string text, LineSelection selection)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var view = new List<byte>();
        foreach (var run in selection.Runs)
        {
            view.AddRange(bytes.AsSpan((int)run.FileOffset, (int)run.Length).ToArray());
            if (run.AddsNewline)
                view.Add((byte)'\n');
        }

        Assert.Equal(view.Count, selection.ViewLength);
        return Encoding.UTF8.GetString(view.ToArray());
    }

    /// <summary>The selected lines of <paramref name="text"/>, each ending in a newline.</summary>
    private static string Naive(string text, IEnumerable<long> lineNumbers)
    {
        var lines = text.Split('\n');
        int count = text.EndsWith('\n') ? lines.Length - 1 : lines.Length;
        return string.Concat(lineNumbers.Where(n => n <= count).Select(n => lines[n - 1] + "\n"));
    }

    [Theory]
    [InlineData("a\nb\nc\nd\ne\n")]
    [InlineData("a\nb\nc\nd\ne")]
    [InlineData("a\r\nbb\r\n\r\nddd\r\ne")]
    public void Shows_exactly_the_selected_lines_in_file_order(string text)
    {
        var selection = Locate(text, SelectionUnit.Lines, (4, 5), (1, 1), (3, 3));

        Assert.Equal(Naive(text, [1, 3, 4, 5]), View(text, selection));
        Assert.Equal([1L, 3L], selection.Runs.ToArray().Select(r => r.FirstLine));
        Assert.Equal([1L, 3L], selection.Runs.ToArray().Select(r => r.LineCount));
        Assert.Equal(4, selection.LineCount);
        Assert.Equal(0, selection.RangesPastEnd);
    }

    [Fact]
    public void Only_a_last_line_with_no_newline_of_its_own_gets_one_added()
    {
        Assert.Equal([false, true], Locate("a\nb\nc", SelectionUnit.Lines, (1, 1), (3, 3)).Runs.ToArray().Select(r => r.AddsNewline));
        Assert.Equal([false, false], Locate("a\nb\nc\n", SelectionUnit.Lines, (1, 1), (3, 3)).Runs.ToArray().Select(r => r.AddsNewline));
    }

    [Theory]
    [InlineData("a\nb\nc\n", 3L)]
    [InlineData("a\nb\nc", 3L)]
    public void A_range_past_the_end_is_cut_short_and_one_beyond_it_dropped(string text, long lastLine)
    {
        var selection = Locate(text, SelectionUnit.Lines, (2, 9), (20, 30), (40, End));

        Assert.Equal(Naive(text, [2, 3]), View(text, selection));
        Assert.Equal(lastLine - 1, selection.Runs[0].LineCount);
        Assert.Equal(2, selection.RangesPastEnd);
    }

    [Theory]
    [InlineData("a\nb\nc\n")]
    [InlineData("a\nb\nc")]
    public void A_range_to_the_end_takes_every_line_left(string text)
    {
        var selection = Locate(text, SelectionUnit.Lines, (2, End));

        Assert.Equal(Naive(text, [2, 3]), View(text, selection));
        Assert.Equal(2, selection.LineCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a\nb\n")]
    public void Nothing_selected_past_the_end_is_an_empty_selection(string text)
    {
        var selection = Locate(text, SelectionUnit.Lines, (3, 4), (9, End));

        Assert.True(selection.IsEmpty);
        Assert.Equal(2, selection.RangesPastEnd);
    }

    [Fact]
    public void Matches_a_naive_split_across_chunk_and_piece_boundaries()
    {
        // Lines of varying length, enough of them to span the 4MB scan chunk more than once.
        var builder = new StringBuilder();
        var random = new Random(11);
        for (int i = 0; i < 200_000; i++)
            builder.Append('x', random.Next(0, 80)).Append('\n');
        string text = builder.ToString();
        byte[] bytes = Encoding.UTF8.GetBytes(text);

        (long, long)[] ranges = [(1, 1), (3, 10), (1_000, 1_000), (60_000, 150_000), (199_999, End)];
        var expected = Naive(text, ranges.SelectMany(r => Enumerable.Range((int)r.Item1, (int)(Math.Min(r.Item2, 200_000) - r.Item1 + 1)).Select(n => (long)n)));

        foreach (IByteSource source in new IByteSource[] { new MemoryByteSource(bytes), new SplitByteSource(bytes, 4093) })
        {
            var selection = Locate(source, SelectionUnit.Lines, ranges);
            Assert.Equal(expected, View(text, selection));
            Assert.Equal([1L, 3L, 1_000L, 60_000L, 199_999L], selection.Runs.ToArray().Select(r => r.FirstLine));
            Assert.Equal(1 + 8 + 1 + 90_001 + 2, selection.LineCount);
        }
    }

    [Fact]
    public async Task Waits_for_a_source_still_arriving_rather_than_ending_early()
    {
        const string text = "a\nb\nc\nd\n";
        var source = new GrowingByteSource(Encoding.UTF8.GetBytes(text), initiallyAvailable: 3);

        var locating = Task.Run(() => Locate(source, SelectionUnit.Lines, (2, 2), (4, End)));
        while (source.Waits == 0)
            Thread.Sleep(1);
        source.Reveal(2);
        source.Seal();

        var selection = await locating;
        Assert.Equal("b\nd\n", View(text, selection));
        Assert.Equal(0, selection.RangesPastEnd);
    }

    [Fact]
    public void Bytes_are_taken_as_given_and_each_run_ends_a_line()
    {
        const string text = "0123456789\nabcdefghij\n";
        var selection = Locate(text, SelectionUnit.Bytes, (2, 4), (11, 21), (30, 40));

        Assert.Equal("234\nabcdefghij\n", View(text, selection));
        Assert.Equal([true, false], selection.Runs.ToArray().Select(r => r.AddsNewline));
        Assert.Equal(1, selection.RangesPastEnd);
        Assert.Equal(0, selection.LineCount);
    }

    [Fact]
    public void A_byte_range_starting_inside_a_character_starts_at_the_character()
    {
        const string text = "ab日本\n";
        var selection = Locate(text, SelectionUnit.Bytes, (3, End));

        Assert.Equal(2, selection.Runs[0].FileOffset);
        Assert.Equal("日本\n", View(text, selection));
    }

    [Fact]
    public void Byte_ranges_within_a_character_of_each_other_become_one_run()
    {
        const string text = "ab日本\n";
        var selection = Locate(text, SelectionUnit.Bytes, (0, 2), (4, 7));

        Assert.Single(selection.Runs.ToArray());
        Assert.Equal("ab日本\n", View(text, selection));
    }

    /// <summary>Counts the bytes a scan is handed, to show a pass skipped what it already knew.</summary>
    private sealed class CountingSource(byte[] all) : IByteSource
    {
        public long BytesRead { get; private set; }

        public long AvailableLength => all.Length;

        public ReadOnlySpan<byte> GetContiguousSpan(long offset, int maxLength)
        {
            var span = offset >= all.Length || maxLength <= 0
                ? ReadOnlySpan<byte>.Empty
                : all.AsSpan((int)offset, (int)Math.Min(maxLength, all.Length - offset));
            BytesRead += span.Length;
            return span;
        }

        public int CopyTo(long offset, Span<byte> destination)
        {
            var span = GetContiguousSpan(offset, destination.Length);
            span.CopyTo(destination);
            return span.Length;
        }
    }

    [Fact]
    public void A_later_pass_starts_from_the_chunks_an_earlier_one_counted()
    {
        // About 18MB of short lines: five 4MB chunks.
        var builder = new StringBuilder();
        var random = new Random(17);
        for (int i = 0; i < 600_000; i++)
            builder.Append('y', random.Next(0, 60)).Append('\n');
        string text = builder.ToString();
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        var checkpoints = new LineCheckpoints();

        var first = new CountingSource(bytes);
        Locate(first, SelectionUnit.Lines, checkpoints, (590_000, 590_010));
        Assert.True(checkpoints.Count > 4);

        // A filter changed to lines near the end, and one near the start: both read about a chunk
        // each rather than the file up to them, and find exactly what a fresh pass finds.
        (long, long)[] changed = [(3, 4), (450_000, 450_002), (599_999, End)];
        var second = new CountingSource(bytes);
        var resumed = Locate(second, SelectionUnit.Lines, checkpoints, changed);
        var fresh = Locate(new MemoryByteSource(bytes), SelectionUnit.Lines, changed);

        Assert.Equal(fresh.Runs.ToArray(), resumed.Runs.ToArray());
        Assert.Equal(Naive(text, [3, 4, 450_000, 450_001, 450_002, 599_999, 600_000]), View(text, resumed));
        // One chunk per place the filter points at, where a fresh pass reads the whole file.
        Assert.True(second.BytesRead <= 3 * 4 * 1024 * 1024, $"read {second.BytesRead:N0} of {bytes.Length:N0} bytes");
    }
}
