using System.Text;
using Argonaut.Features.Raw;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// What an insert does to the row boundaries after it, <i>inside a single soft-wrapped line</i>.
/// This is the property every attempt to bound the long-line re-derivation has foundered on, and
/// the answer is counter-intuitive enough that it is pinned here rather than argued about.
///
/// A forced break falls <see cref="RawSegmentIndex.WrapWidth"/> bytes from the row start, backed
/// off up to 3 bytes so a multi-byte character is not split. Those two rules pull in opposite
/// directions when bytes are inserted earlier in the line:
///
/// - Over <b>ASCII</b> there is never any backoff, so the breaks are pure arithmetic from the row
///   start and land on exactly the same absolute offsets as before. The content at those offsets
///   has shifted, but the boundaries have not.
/// - Over <b>multi-byte</b> content the backoff follows the characters, so the breaks move with
///   the content - to the old offset plus the number of bytes inserted.
///
/// Which is why the re-derivation cannot stop early. Its convergence test looks for the second
/// case (offsets differing by exactly the byte delta), and ASCII - which is what a large JSON or
/// log file is - produces the first, right up until the line's real newline forces them back
/// together. And the first case cannot be treated as a convergence of its own, because the bytes
/// being read there are <i>not</i> the bytes the original index was built over, so nothing about
/// the boundaries beyond the next one is proven. See docs/roadmap.md.
/// </summary>
public class RawLongLineReflowTests
{
    private const int WrapWidth = 80;
    private const int LineBytes = 200_000;
    private const long EditAt = 1_000;
    private const long ProbeFrom = 2_000;
    private const int BoundariesProbed = 200;

    private static List<long> RowStartsFrom(IByteSource source, long from, int count)
    {
        var starts = new List<long>(count);
        long at = from;
        for (int i = 0; i < count && at < source.AvailableLength; i++)
        {
            starts.Add(at);
            at = RawRowBoundary.Next(source, WrapWidth, at).End;
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
    public void OverMultiByteContent_BoundariesPastAnInsertMoveWithTheContent()
    {
        var (before, after) = BoundariesEitherSideOfAnInsert("abcdéfghij");

        // Not every one, and not from the first boundary: a break that happens to fall clear of
        // a character does not move, and whether it does depends on where the walk started. The
        // shift emerges over the run, which is why this is asserted in aggregate rather than at
        // one index - measured, 194 of 200.
        int movedByTheInsert = before.Zip(after, (b, a) => a == b + 1).Count(moved => moved);
        Assert.True(movedByTheInsert > before.Count * 9 / 10,
            $"only {movedByTheInsert} of {before.Count} boundaries moved with the content");
    }
}
