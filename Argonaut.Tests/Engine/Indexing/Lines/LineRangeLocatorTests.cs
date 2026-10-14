using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Lines;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Engine.Indexing.Lines;

/// <summary>
/// Verifies <see cref="LineRangeLocator"/> against a naive split on <c>\n</c>: the byte range
/// covers exactly the lines asked for, newline included, and is cut short at the end of the
/// document - over a whole source, one served in small pieces, and one still arriving.
/// </summary>
public class LineRangeLocatorTests
{
    private static LocatedLines? Locate(string text, long first, long? last)
        => LineRangeLocator.Locate(new MemoryByteSource(Encoding.UTF8.GetBytes(text)), first, last);

    private static string Slice(string text, LocatedLines located)
        => text.Substring((int)located.Range.Offset, (int)located.Range.Length);

    [Theory]
    [InlineData("a\nb\nc\nd\n", 2, 3L, "b\nc\n", 3L)]
    [InlineData("a\nb\nc\nd\n", 1, 1L, "a\n", 1L)]
    [InlineData("a\nb\nc\nd\n", 4, 4L, "d\n", 4L)]
    [InlineData("a\nb\nc\nd\n", 3, 99L, "c\nd\n", 4L)]
    [InlineData("a\nb\nc\nd", 3, 99L, "c\nd", 4L)]
    [InlineData("a\nb\nc\nd", 4, 4L, "d", 4L)]
    [InlineData("a\r\nb\r\nc\r\n", 2, 2L, "b\r\n", 2L)]
    [InlineData("\n\n\n", 2, 3L, "\n\n", 3L)]
    public void Covers_exactly_the_lines_asked_for(string text, long first, long? last, string expected, long? expectedLast)
    {
        var located = Locate(text, first, last)!.Value;

        Assert.Equal(expected, Slice(text, located));
        Assert.Equal(first, located.FirstLine);
        Assert.Equal(expectedLast, located.LastLine);
    }

    [Theory]
    [InlineData("a\nb\nc\n", 2, "b\nc\n")]
    [InlineData("a\nb\nc", 3, "c")]
    [InlineData("a\nb\nc", 1, "a\nb\nc")]
    public void An_open_ended_range_runs_to_the_end_without_a_last_line(string text, long first, string expected)
    {
        var located = Locate(text, first, null)!.Value;

        Assert.Equal(expected, Slice(text, located));
        Assert.Null(located.LastLine);
    }

    [Theory]
    [InlineData("a\nb\n", 3)]
    [InlineData("a\nb", 3)]
    [InlineData("", 1)]
    public void A_first_line_past_the_end_is_not_found(string text, long first)
    {
        Assert.Null(Locate(text, first, null));
        Assert.Null(Locate(text, first, first + 5));
    }

    [Fact]
    public void Matches_a_naive_split_across_chunk_and_piece_boundaries()
    {
        // Lines of varying length, enough of them to span the 4MB scan chunk more than once.
        var builder = new StringBuilder();
        var random = new Random(7);
        for (int i = 0; i < 200_000; i++)
            builder.Append('x', random.Next(0, 80)).Append('\n');
        string text = builder.ToString();
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        var lines = text.Split('\n');

        foreach (var (first, last) in new[] { (1L, 1L), (1_000L, 150_000L), (123_456L, 123_456L), (199_999L, 200_000L) })
        {
            string expected = string.Concat(lines.Skip((int)first - 1).Take((int)(last - first + 1)).Select(l => l + "\n"));

            foreach (IByteSource source in new IByteSource[] { new MemoryByteSource(bytes), new SplitByteSource(bytes, 4093) })
            {
                var located = LineRangeLocator.Locate(source, first, last)!.Value;
                Assert.Equal(expected, Slice(text, located));
                Assert.Equal(last, located.LastLine);
            }
        }
    }

    [Fact]
    public void Waits_for_a_growing_source_rather_than_stopping_at_what_has_arrived()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("a\nb\nc\nd\ne\n");
        var source = new GrowingByteSource(bytes, initiallyAvailable: 3);

        var locating = Task.Run(() => LineRangeLocator.Locate(source, 2, null));
        while (source.Waits == 0)
            Thread.Sleep(1);
        source.Reveal(4);
        source.Seal();

        var located = locating.GetAwaiter().GetResult()!.Value;
        Assert.Equal(new ByteRange(2, 8), located.Range);
    }

    [Fact]
    public void Rejects_a_range_that_ends_before_it_starts()
    {
        var source = new MemoryByteSource("a\n"u8.ToArray());

        Assert.Throws<ArgumentOutOfRangeException>(() => LineRangeLocator.Locate(source, 0, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => LineRangeLocator.Locate(source, 3, 2));
    }
}
