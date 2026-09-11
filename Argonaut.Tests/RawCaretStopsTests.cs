using System.Text;
using Argonaut.Features.Raw;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Caret movement is tested as a property rather than a list of cases: walking forward from 0
/// must land only on character boundaries, strictly advance, terminate at the end, and walking
/// back must retrace the identical path. A caret that can get stuck, skip a position, or land
/// inside a character fails at least one of those on some input, so they are asserted over
/// text, multibyte, CRLF and binary content alike.
///
/// These need no temp file: since the index reads an <see cref="IByteSource"/>, an array can be
/// indexed directly.
/// </summary>
public class RawCaretStopsTests
{
    private static (RawSegmentIndex Index, IByteSource Source) Indexed(byte[] content, int wrapWidth = 80)
    {
        var source = new ArrayByteSource(content);
        var index = RawSegmentIndex.StartIndexing(source, wrapWidth);
        index.IndexingTask.GetAwaiter().GetResult();
        return (index, source);
    }

    private static (RawSegmentIndex Index, IByteSource Source) Indexed(string text, int wrapWidth = 80)
        => Indexed(Encoding.UTF8.GetBytes(text), wrapWidth);

    private static List<long> WalkForward(RawSegmentIndex index, IByteSource source)
    {
        var stops = new List<long> { 0 };
        long offset = 0;
        while (offset < source.Length)
        {
            long next = RawCaretStops.Next(index, source, offset);
            Assert.True(next > offset, $"Next stalled at {offset}");
            stops.Add(next);
            offset = next;
        }

        return stops;
    }

    [Fact]
    public void Ascii_StopsAtEveryByteAndAtTheEnd()
    {
        var (index, source) = Indexed("abc");

        Assert.Equal([0, 1, 2, 3], WalkForward(index, source));
    }

    [Fact]
    public void MultiByteCharacters_AreSteppedOverWhole()
    {
        // 'é' is two bytes each, so the interior bytes 1, 3 and 5 are not stops.
        var (index, source) = Indexed("ééé");

        Assert.Equal([0, 2, 4, 6], WalkForward(index, source));
    }

    [Fact]
    public void SurrogatePair_IsOneStep()
    {
        var (index, source) = Indexed("\U0001F600x");

        Assert.Equal([0, 4, 5], WalkForward(index, source));
    }

    [Fact]
    public void InvalidByteRun_IsOneStep()
    {
        // 0xC3 is a truncated two-byte sequence: one U+FFFD, so one caret stop covering it.
        var (index, source) = Indexed([0x61, 0xC3, 0x28, 0x62]);

        Assert.Equal([0, 1, 2, 3, 4], WalkForward(index, source));
    }

    [Fact]
    public void LineFeed_IsNotAStop()
    {
        // "ab\ncd": offset 2 is after 'b', offset 3 is before 'c'. The newline byte itself is
        // never a caret position.
        var (index, source) = Indexed("ab\ncd");

        Assert.Equal([0, 1, 2, 3, 4, 5], WalkForward(index, source));
    }

    [Fact]
    public void CarriageReturnLineFeed_IsSkippedAsAPair()
    {
        // "ab\r\ncd": offset 3, between '\r' and '\n', must not be reachable.
        var (index, source) = Indexed("ab\r\ncd");

        var stops = WalkForward(index, source);

        Assert.Equal([0, 1, 2, 4, 5, 6], stops);
        Assert.DoesNotContain(3L, stops);
    }

    [Fact]
    public void SoftWrappedRows_DoNotDuplicateTheSharedBoundary()
    {
        // A forced break at the cap: the end of one row and the start of the next are the same
        // offset, so it must appear exactly once in the walk.
        var (index, source) = Indexed(new string('x', 20), wrapWidth: 8);

        var stops = WalkForward(index, source);

        Assert.Equal(21, stops.Count);
        Assert.Equal(stops, new HashSet<long>(stops));
    }

    [Fact]
    public void EmptyDocument_HasOneStop()
    {
        var (index, source) = Indexed(string.Empty);

        Assert.Equal(0, RawCaretStops.Next(index, source, 0));
        Assert.Equal(0, RawCaretStops.Previous(index, source, 0));
    }

    [Fact]
    public void MovementIsClampedToTheDocument()
    {
        var (index, source) = Indexed("abc");

        Assert.Equal(0, RawCaretStops.Previous(index, source, -5));
        Assert.Equal(0, RawCaretStops.Previous(index, source, 0));
        Assert.Equal(3, RawCaretStops.Next(index, source, 3));
        Assert.Equal(3, RawCaretStops.Next(index, source, 99));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("ééé")]
    [InlineData("ab\ncd\nef")]
    [InlineData("ab\r\ncd\r\n")]
    [InlineData("\U0001F600é\tx\n\U0001F600")]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxx")]
    public void PreviousExactlyRetracesNext(string text)
    {
        var (index, source) = Indexed(text, wrapWidth: 8);

        var forward = WalkForward(index, source);
        for (int i = forward.Count - 1; i > 0; i--)
        {
            long back = RawCaretStops.Previous(index, source, forward[i]);
            Assert.Equal(forward[i - 1], back);
        }
    }

    [Fact]
    public void Snap_LeavesALegalPositionAlone()
    {
        var (index, source) = Indexed("aéb");

        Assert.Equal(1, RawCaretStops.Snap(index, source, 1, CaretSnap.Backward));
        Assert.Equal(1, RawCaretStops.Snap(index, source, 1, CaretSnap.Forward));
    }

    [Fact]
    public void Snap_PullsAMidCharacterOffsetToACharacterBoundary()
    {
        // Byte 2 is the second byte of 'é' - what an offset arriving from outside can point at.
        var (index, source) = Indexed("aéb");

        Assert.Equal(1, RawCaretStops.Snap(index, source, 2, CaretSnap.Backward));
        Assert.Equal(3, RawCaretStops.Snap(index, source, 2, CaretSnap.Forward));
    }

    /// <summary>
    /// The property, over content that is not text at all: forced breaks landing mid-character
    /// and long invalid runs are where movement rules break down.
    /// </summary>
    [Fact]
    public void OverBinaryContent_EveryStopIsACharacterBoundaryAndTheWalkRetraces()
    {
        var rng = new Random(4242);
        byte[] content = new byte[64 * 1024];
        rng.NextBytes(content);
        for (int i = 0; i < content.Length; i += rng.Next(1, 200))
            content[i] = (byte)'\n';

        var (index, source) = Indexed(content, wrapWidth: 80);

        long offset = 0;
        long previousStop = -1;
        while (offset < source.Length)
        {
            int rowIndex = index.RowForOffset(offset)!.Value;
            var info = index.GetRowInfo(rowIndex);
            var row = RawRowDecoder.Decode(source, info.Start, info.End, info.IsSoftWrapped);

            Assert.True(
                row.IsCharacterBoundary((int)(offset - info.Start)),
                $"offset {offset} is not a character boundary in row {rowIndex}");

            if (previousStop >= 0)
                Assert.Equal(previousStop, RawCaretStops.Previous(index, source, offset));

            previousStop = offset;
            offset = RawCaretStops.Next(index, source, offset);
            Assert.True(offset > previousStop, $"Next stalled at {previousStop}");
        }
    }
}
