using System;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Raw;

/// <summary>Which way to pull an offset that is not already a legal caret position.</summary>
public enum CaretSnap
{
    /// <summary>Towards the start of the document.</summary>
    Backward,

    /// <summary>Towards the end of the document.</summary>
    Forward
}

/// <summary>
/// Where a caret is allowed to sit, and how it steps between those places.
///
/// A caret in this viewer is a byte offset, because that is the coordinate an edit and the row
/// index both speak - but most byte offsets are not places a caret can be. It must never land
/// inside a multi-byte character, between the two halves of a surrogate pair, inside a run of
/// invalid bytes that drew as a single U+FFFD, or between the '\r' and '\n' of a line ending.
/// The last two matter most for the case this editor exists to serve: in a corrupted file, a
/// broken byte sequence should be one thing the user can delete, not a region the caret gets
/// lost inside.
///
/// Newline bytes are inside a row's range but are not drawn, so they hold no caret either -
/// stepping right off the end of a line goes to the first byte of the next row, skipping them.
/// On a soft-wrapped row there is nothing to skip: the end of one row and the start of the next
/// are the same offset, which is what caret affinity exists to disambiguate for <i>display</i>.
/// Movement does not need affinity, so nothing here takes it.
/// </summary>
public static class RawCaretStops
{
    /// <summary>
    /// The nearest legal caret position to <paramref name="offset"/>, moving in
    /// <paramref name="direction"/> if it is not already one. Used when an offset arrives from
    /// outside - a JSON token's offset, or a parse failure's - and may point mid-character.
    /// </summary>
    public static long Snap(IRawRowIndex index, IByteSource source, long offset, CaretSnap direction)
    {
        long clamped = Math.Clamp(offset, 0, source.AvailableLength);
        if (!TryDecodeRowAt(index, source, clamped, out var row, out var info))
            return clamped;

        int within = (int)(clamped - info.Start);
        if (within <= row.DisplayByteLength && row.IsCharacterBoundary(within))
            return clamped;

        return direction == CaretSnap.Backward
            ? info.Start + row.ByteOffsetForChar(row.CharIndexForByte(within))
            : Next(index, source, clamped);
    }

    /// <summary>The next legal caret position after <paramref name="offset"/>, or the end of the
    /// document when there is none.</summary>
    public static long Next(IRawRowIndex index, IByteSource source, long offset)
    {
        if (offset >= source.AvailableLength)
            return source.AvailableLength;

        long from = Math.Max(offset, 0);
        if (!TryDecodeRowAt(index, source, from, out var row, out var info))
            return source.AvailableLength;

        int within = (int)(from - info.Start);

        // At or past the last drawn byte - i.e. sitting in the row's newline, which holds no
        // caret - so the next stop is the first byte of the next row.
        if (within >= row.DisplayByteLength)
            return Math.Min(info.End, source.AvailableLength);

        // The smallest boundary STRICTLY greater than `within`. Strictly matters: the two halves
        // of a surrogate pair report the same byte offset, so taking "the next char" would hand
        // back the offset we started on and the caret would stall.
        int charIndex = row.CharIndexForByte(within) + 1;
        while (charIndex < row.Text.Length && row.ByteOffsetForChar(charIndex) <= within)
            charIndex++;

        return info.Start + row.ByteOffsetForChar(Math.Min(charIndex, row.Text.Length));
    }

    /// <summary>The previous legal caret position before <paramref name="offset"/>, or 0.</summary>
    public static long Previous(IRawRowIndex index, IByteSource source, long offset)
    {
        long from = Math.Min(offset, source.AvailableLength);
        if (from <= 0)
            return 0;

        // Past the end of the last row there is no row to land in; step back into the last one.
        int rowIndex = index.RowForOffset(from) ?? index.RowCount - 1;

        // Walk back a row at a time until one of them holds a stop below `from`. More than one
        // step is possible: a soft-wrapped row's end and the next row's start are the SAME
        // offset, so the row we begin in can have nothing strictly below `from` at all.
        while (rowIndex >= 0)
        {
            var info = index.GetRowInfo(rowIndex);
            var row = RawRowDecoder.Decode(source, info.Start, info.End, info.IsSoftWrapped);
            if (GreatestStopBelow(row, info, from) is long stop)
                return stop;

            rowIndex--;
        }

        return 0;
    }

    /// <summary>
    /// The largest legal caret position in this row that is strictly below
    /// <paramref name="from"/>, or null when the row holds none.
    /// </summary>
    private static long? GreatestStopBelow(RawDecodedRow row, RawRowInfo info, long from)
    {
        if (from <= info.Start)
            return null;

        // `from` is inside the row's newline bytes, so the end of the drawn text is below it.
        if (from > info.Start + row.DisplayByteLength)
            return info.Start + row.DisplayByteLength;

        int within = (int)(from - info.Start);
        int charIndex = row.CharIndexForByte(within);

        // CharIndexForByte lands on the character CONTAINING the byte, which is at or after
        // `within` when `within` is already a boundary - so step back to get strictly below.
        while (charIndex >= 0 && row.ByteOffsetForChar(Math.Min(charIndex, row.Text.Length)) >= within)
            charIndex--;

        return charIndex < 0 ? null : info.Start + row.ByteOffsetForChar(charIndex);
    }

    private static bool TryDecodeRowAt(
        IRawRowIndex index,
        IByteSource source,
        long offset,
        out RawDecodedRow row,
        out RawRowInfo info)
    {
        int? rowIndex = index.RowForOffset(offset);
        if (rowIndex is null)
        {
            row = null!;
            info = default;
            return false;
        }

        info = index.GetRowInfo(rowIndex.Value);
        row = RawRowDecoder.Decode(source, info.Start, info.End, info.IsSoftWrapped);
        return true;
    }
}
