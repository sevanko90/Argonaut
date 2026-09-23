using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Features.Raw;

namespace Argonaut.Tests;

/// <summary>
/// What an insert does to the row boundaries after it, <i>inside a single soft-wrapped line</i>.
/// This is the property every attempt to bound the long-line re-derivation foundered on, so it is
/// pinned here rather than argued about.
///
/// Forced breaks are cap-anchored (<see cref="RawRowBoundary"/>): the caps sit at
/// <c>lineStart + k·W</c> and each break backs off from its own cap by 0..3 bytes, depending only
/// on the bytes at that cap. So an insert earlier in the line can move a later boundary by at most
/// the backoff range, and cannot chain:
///
/// - Over <b>ASCII</b> there is never any backoff, so every later boundary stays at exactly the
///   same absolute offset.
/// - Over <b>multi-byte</b> content the backoff at a cap changes with the bytes now sitting there,
///   so a boundary moves by up to 3 - and the next one is measured from the next cap regardless.
///
/// Before the rule was cap-anchored a break was measured from the previous row's end, and over
/// multi-byte content the boundaries moved <i>with the content</i>, all the way to the line's
/// newline; that is what made typing early in a 100MB line cost ~40ms.
/// </summary>
public class RawLongLineReflowTests
{
    private const int WrapWidth = 80;
    private const int LineBytes = 200_000;
    private const long EditAt = 1_000;
    private const long ProbeFrom = 2_000;
    private const int BoundariesProbed = 200;

    /// <summary>
    /// Starts of <paramref name="count"/> rows from the row whose cap-grid position is
    /// <paramref name="from"/>, walking the line from its start - the only place a walk can begin
    /// that knows where the caps are. Selected by row number rather than offset, so the same row
    /// is compared on both sides of the insert even when its start moves across the probe point.
    /// </summary>
    private static List<long> RowStartsFrom(IByteSource source, long from, int count)
    {
        var starts = new List<long>(count);
        var cursor = RawRowCursor.StartOfLine(0, 1, WrapWidth);
        for (long row = 0; starts.Count < count && cursor.Start < source.AvailableLength; row++)
        {
            if (row >= from / WrapWidth)
                starts.Add(cursor.Start);

            cursor.Advance(source, WrapWidth);
        }

        return starts;
    }

    private static (List<long> Before, List<long> After) BoundariesEitherSideOfAnInsert(string filler)
    {
        var text = new StringBuilder();
        while (Encoding.UTF8.GetByteCount(text.ToString()) < LineBytes)
            text.Append(filler);

        // Deliberately no newline anywhere: this is the shape of a minified document.
        var original = new MemoryByteSource(Encoding.UTF8.GetBytes(text.ToString()));
        var edited = new RawPieceTable(original);
        edited.Insert(EditAt, "Z"u8);

        return (RowStartsFrom(original, ProbeFrom, BoundariesProbed),
                RowStartsFrom(edited, ProbeFrom, BoundariesProbed));
    }

    [Fact]
    public void OverAscii_BoundariesPastAnInsertStayAtTheSameOffsets()
    {
        var (before, after) = BoundariesEitherSideOfAnInsert("abcdefghij");

        Assert.Equal(before, after);
    }

    [Fact]
    public void OverMultiByteContent_BoundariesPastAnInsertMoveByAtMostTheBackoff()
    {
        var (before, after) = BoundariesEitherSideOfAnInsert("abcdéfghij日");

        Assert.Equal(before.Count, after.Count);
        for (int i = 0; i < before.Count; i++)
            Assert.InRange(after[i] - before[i], -RawRowBoundary.MaxUtf8Backoff, RawRowBoundary.MaxUtf8Backoff);

        // And the insert really did change some of them - otherwise this is the ASCII case again
        // and proves nothing about multi-byte content.
        Assert.Contains(before.Zip(after), pair => pair.First != pair.Second);
    }
}
