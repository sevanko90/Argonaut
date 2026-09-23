using System;
using Argonaut.Engine.Bytes;

namespace Argonaut.Features.Raw;

/// <summary>
/// The display rows of one line, as arithmetic from where the line starts and ends rather than a
/// walk. Possible only because forced breaks are cap-anchored (<see cref="RawRowBoundary"/>): the
/// caps sit at <c>lineStart + k·W</c> whatever the backoffs were, so row <i>k</i> starts at cap
/// <i>k</i> less the backoff there, and nothing about it depends on the rows before.
///
/// It must agree byte for byte with a <see cref="RawRowCursor"/> walk over the same line -
/// <c>RawLineRowsTests</c> checks exactly that over content chosen to put every break rule on a
/// cap. Counting rows needs no bytes at all; locating one peeks at most four.
/// </summary>
/// <param name="LineStart">Where the line begins.</param>
/// <param name="ContentEnd">Offset of the line's '\n', or the data length when the line is
/// unterminated.</param>
/// <param name="Terminated">Whether the line ends in '\n' (the last line of the data may not).</param>
/// <param name="WrapWidth">The row cap the rows were derived at.</param>
internal readonly record struct RawLineRows(long LineStart, long ContentEnd, bool Terminated, int WrapWidth)
{
    /// <summary>
    /// Rows the line occupies: one per cap strictly inside its content (each is a soft break),
    /// plus the last row. An empty terminated line is one row holding the '\n'; an empty
    /// unterminated line - only ever the phantom one after a trailing '\n' - has none.
    /// </summary>
    public int Count
    {
        get
        {
            long content = ContentEnd - LineStart;
            if (content <= 0)
                return Terminated ? 1 : 0;

            return (int)((content + WrapWidth - 1) / WrapWidth);
        }
    }

    /// <summary>Exclusive end of the line, the '\n' included.</summary>
    public long End => Terminated ? ContentEnd + 1 : ContentEnd;

    /// <summary>Where row <paramref name="row"/> starts. At most four bytes read.</summary>
    public long Start(IByteSource source, int row)
    {
        if (row == 0)
            return LineStart;

        long cap = LineStart + ((long)row * WrapWidth);
        return cap - RawRowBoundary.BackoffAt(source, cap);
    }

    /// <summary>Byte range and wrap state of row <paramref name="row"/>.</summary>
    public (long Start, long End, bool SoftWrap) Range(IByteSource source, int row)
    {
        int last = Count - 1;
        long start = Start(source, row);
        return row >= last
            ? (start, End, false)
            : (start, Start(source, row + 1), true);
    }

    /// <summary>
    /// The row holding <paramref name="offset"/>, which must lie within the line. The cap
    /// arithmetic lands on the right row or the one before it, because a backoff moves a row's
    /// start back by at most 3 bytes and a row is at least W-3 long.
    /// </summary>
    public int RowContaining(IByteSource source, long offset)
    {
        int last = Math.Max(Count - 1, 0);
        int row = (int)Math.Min((offset - LineStart) / WrapWidth, last);
        if (row < last && offset >= Start(source, row + 1))
            row++;

        return row;
    }
}
