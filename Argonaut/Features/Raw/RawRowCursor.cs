using Argonaut.Infrastructure;

namespace Argonaut.Features.Raw;

/// <summary>
/// Position state at a row start: enough to derive every row after it. Every walk over rows goes
/// through this, so the cap a forced break is measured from travels with the walk rather than
/// being re-derived from where the previous row happened to end - see <see cref="RawRowBoundary"/>
/// for why that distinction is the whole design.
///
/// A mutable struct on purpose: a walk that may have to abandon a step (the scan over a source
/// still receiving bytes) copies it, advances the copy, and keeps it only if the step stands.
/// </summary>
internal struct RawRowCursor
{
    /// <summary>Where the current row starts.</summary>
    public long Start;

    /// <summary><c>lineStart + k·W</c> for the next forced break.</summary>
    public long NextCap;

    /// <summary>Whether the current row begins a real line.</summary>
    public bool AtLineStart;

    /// <summary>1-based number of the line the current row sits in.</summary>
    public int LineNumber;

    /// <summary>A cursor on the first row of a line.</summary>
    public static RawRowCursor StartOfLine(long start, int lineNumber, int wrapWidth)
        => new() { Start = start, NextCap = start + wrapWidth, AtLineStart = true, LineNumber = lineNumber };

    /// <summary>
    /// How far the current row's start sits before the cap it was broken at: 0 on a line start,
    /// 0..3 on a continuation row. What an anchor stores so a walk can resume here.
    /// </summary>
    public readonly int BackoffFromCap(int wrapWidth) => (int)(NextCap - wrapWidth - Start);

    /// <summary>Ends the row at <see cref="Start"/> and moves to the next one.</summary>
    public (long End, bool SoftWrap) Advance(IByteSource source, int wrapWidth)
    {
        var (end, softWrap, nextCap) = RawRowBoundary.Next(source, wrapWidth, Start, NextCap);
        Start = end;
        NextCap = nextCap;
        if (softWrap)
        {
            AtLineStart = false;
        }
        else
        {
            LineNumber++;
            AtLineStart = true;
        }

        return (end, softWrap);
    }
}
