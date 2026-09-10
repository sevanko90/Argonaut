using System;

namespace Argonaut.Features.Raw;

/// <summary>
/// Which of the two rows sharing a soft-wrap boundary a caret belongs to.
///
/// A forced break makes one byte offset the end of one row and the start of the next at the same
/// time. Movement does not care - both are the same position in the document - but drawing very
/// much does, and so does the user: pressing End on a wrapped row should leave the caret at the
/// right-hand edge of that row, not blinking at the left edge of the row below.
/// </summary>
public enum CaretAffinity
{
    /// <summary>Draw at the start of the following row. Where Home, and arrowing right across the
    /// boundary, leave the caret.</summary>
    Downstream,

    /// <summary>Draw at the end of the preceding row. Where End, and arrowing left across the
    /// boundary, leave the caret.</summary>
    Upstream
}

/// <summary>
/// A caret: a byte offset in the document, plus which side of a wrap boundary it is drawn on.
/// Byte offsets rather than (row, column) because that is the coordinate an edit and the row
/// index both speak, and because it survives everything that renumbers rows - a wrap-width
/// change, a background scan publishing more of the file, an edit earlier in the document.
/// </summary>
public readonly record struct RawCaret(long Offset, CaretAffinity Affinity = CaretAffinity.Downstream);

/// <summary>
/// A selected byte range, as the offset the selection was anchored at and the offset the caret
/// has since moved to. Kept as anchor-and-active rather than start-and-end so extending a
/// selection backwards past its own start reverses direction correctly.
///
/// Two longs, so selecting an entire multi-GB document costs nothing to hold; whether anything
/// can be <i>done</i> with a selection that large is a separate question, answered by
/// <see cref="RawTextExtractor"/>'s cap.
/// </summary>
public readonly record struct RawSelection(long Anchor, long Active)
{
    public long Start => Math.Min(Anchor, Active);

    public long End => Math.Max(Anchor, Active);

    public long Length => End - Start;

    public bool IsEmpty => Anchor == Active;

    /// <summary>An empty selection sitting at <paramref name="offset"/>.</summary>
    public static RawSelection At(long offset) => new(offset, offset);

    /// <summary>
    /// The part of this selection that falls inside [<paramref name="rangeStart"/>,
    /// <paramref name="rangeEnd"/>), or null when none of it does. How a row works out which
    /// slice of a multi-row selection to paint.
    /// </summary>
    public (long Start, long End)? Intersect(long rangeStart, long rangeEnd)
    {
        long start = Math.Max(Start, rangeStart);
        long end = Math.Min(End, rangeEnd);
        return start < end ? (start, end) : null;
    }
}
