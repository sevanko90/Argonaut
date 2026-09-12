using System;
using System.Buffers;
using System.Text;
using Argonaut.Infrastructure;
using Argonaut.Infrastructure.Unicode;

namespace Argonaut.Features.Raw;

/// <summary>
/// What the raw view's status gutter says about the caret: which character it sits on, where that
/// is, and how much is selected.
///
/// Every number here is either free or bounded on purpose. The byte offset is free - it is what
/// the caret already is. The line number is nearly free: the row index computes it walking from a
/// row's anchor anyway (<see cref="IRawRowIndex.LineContaining"/>), so it is never capped and
/// never disappears. The character under the caret costs one decode of at most four bytes.
///
/// The column is the one real question. It is a character count from the start of the line, and a
/// line here is unbounded - one minified JSON document is a single line of several GB - so it is
/// capped (<see cref="ColumnScanBytes"/>) and reported as null past that, which the gutter shows
/// as a dash beside a line number that is still correct. A character count for the selection is
/// capped the same way (<see cref="SelectionScanBytes"/>).
///
/// There is deliberately no character offset into the file. It cannot be answered without
/// decoding from byte 0, and the scan that builds the row index finds rows with a vectorized
/// newline search that never decodes at all - so the number would cost either a full decode per
/// caret move or a permanently slower index.
/// </summary>
public readonly record struct RawCaretReadout(
    long ByteOffset,
    int? LineNumber,
    int? Column,
    string Character,
    long SelectionBytes,
    long? SelectionCharacters)
{
    /// <summary>
    /// How far back from the caret the line start may be for a column to be offered.
    ///
    /// Both halves of the work over that span are vectorized - <c>LastIndexOf</c> for the line
    /// start, <see cref="Ascii.IsValid(ReadOnlySpan{byte})"/> for the character count - so the cap
    /// can sit a long way out. Measured at the cap, Release, per call: 0.028ms for ASCII, 1.0ms
    /// when the span is not ASCII and every rune has to be decoded, and unmeasurable for a normal
    /// column of a hundred or so. Only a caret a megabyte into one unbroken line gives up.
    /// </summary>
    public const int ColumnScanBytes = 1024 * 1024;

    /// <summary>How much selection may be decoded to count its characters.</summary>
    public const int SelectionScanBytes = 1024 * 1024;

    /// <summary>Shown when the caret is at end of file, where there is no character to describe.</summary>
    public const string EndOfFile = "end of file";

    /// <summary>
    /// Reads the document around <paramref name="caret"/>. Cheap enough to call on every caret
    /// move: one four-byte decode, plus a bounded walk back to the line start.
    /// </summary>
    public static RawCaretReadout Describe(
        IRawRowIndex rows, IByteSource source, RawCaret caret, RawSelection selection)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(source);

        long offset = Math.Clamp(caret.Offset, 0, source.AvailableLength);
        var (lineNumber, column) = LineAndColumn(rows, source, offset);

        return new RawCaretReadout(
            offset,
            lineNumber,
            column,
            DescribeCharacterAt(source, offset),
            selection.Length,
            CountSelectedCharacters(source, selection));
    }

    /// <summary>
    /// The character at <paramref name="offset"/> as "U+2028 LINE SEPARATOR". Reads the real
    /// bytes rather than the row's display text, so it names what is in the file and not the
    /// glyph standing in for it - which is the entire point of showing it for a control or a
    /// separator.
    /// </summary>
    private static string DescribeCharacterAt(IByteSource source, long offset)
    {
        if (offset >= source.AvailableLength)
            return EndOfFile;

        Span<byte> encoded = stackalloc byte[4];
        int read = source.CopyTo(offset, encoded);
        if (read <= 0)
            return EndOfFile;

        var status = Rune.DecodeFromUtf8(encoded[..read], out Rune rune, out _);
        if (status != OperationStatus.Done)
            return $"0x{encoded[0]:X2} invalid UTF-8";

        return $"U+{rune.Value:X4} {UnicodeNames.NameOf(rune.Value) ?? Unnamed(rune.Value)}";
    }

    /// <summary>
    /// Code points the Unicode name table has nothing for. Noncharacters are called out
    /// separately from unassigned ones because they are permanently reserved rather than
    /// merely not allocated yet, and seeing one in a file usually means something upstream
    /// mangled it.
    /// </summary>
    private static string Unnamed(int codePoint)
        => (codePoint & 0xFFFE) == 0xFFFE || codePoint is >= 0xFDD0 and <= 0xFDEF
            ? "noncharacter"
            : "unassigned";

    /// <summary>
    /// The line the caret is on, and its 1-based character column within that line.
    ///
    /// The two are answered independently, because they cost different things. The line number is
    /// the row index's to give and is never capped. The column needs the line's first byte, found
    /// by scanning back for a newline rather than by walking rows - a row walk costs an anchor
    /// resolution each, while the byte scan is one vectorized <c>LastIndexOf</c> per chunk - and
    /// then a character count over that span. Past <see cref="ColumnScanBytes"/> the column is
    /// null, and the line number still is not.
    /// </summary>
    private static (int? LineNumber, int? Column) LineAndColumn(
        IRawRowIndex rows, IByteSource source, long offset)
    {
        if (rows.RowForOffset(offset) is not int rowIndex)
            return (null, null);

        int? lineNumber = rows.LineContaining(rowIndex);
        if (StartOfLine(source, offset) is not long lineStart)
            return (lineNumber, null);

        return (lineNumber, 1 + CountCharacters(source, lineStart, offset));
    }

    /// <summary>
    /// The first byte of the line holding <paramref name="offset"/>, or null when no line start
    /// is within <see cref="ColumnScanBytes"/> of it. Scans backwards in chunks so a caret early
    /// in the file reads a few bytes rather than a fixed window.
    /// </summary>
    private static long? StartOfLine(IByteSource source, long offset)
    {
        const int ChunkBytes = 64 * 1024;

        long limit = Math.Max(0, offset - ColumnScanBytes);
        long at = offset;

        // Rented rather than allocated: this runs on every caret move, and a fresh 64KB array per
        // keystroke is exactly the garbage this app is built to avoid. Only the gather path uses
        // it - over a plain mapping the scan reads the bytes where they lie.
        byte[]? gathered = null;
        try
        {
            while (at > limit)
            {
                int length = (int)Math.Min(ChunkBytes, at - limit);
                long from = at - length;

                var window = source.GetContiguousSpan(from, length);
                if (window.Length < length)
                {
                    gathered ??= ArrayPool<byte>.Shared.Rent(ChunkBytes);
                    window = gathered.AsSpan(0, source.CopyTo(from, gathered.AsSpan(0, length)));
                }

                int newline = window.LastIndexOf((byte)'\n');
                if (newline >= 0)
                    return from + newline + 1;

                at = from;
            }
        }
        finally
        {
            if (gathered is not null)
                ArrayPool<byte>.Shared.Return(gathered);
        }

        // The start of the file is a line start; anything else means the cap ran out first.
        return limit == 0 ? 0 : null;
    }

    /// <summary>
    /// Characters in a selection, or null when it is longer than <see cref="SelectionScanBytes"/>
    /// - a select-all on a multi-GB file is one keystroke, and counting its characters is a full
    /// decode of the document.
    /// </summary>
    private static long? CountSelectedCharacters(IByteSource source, RawSelection selection)
    {
        if (selection.IsEmpty)
            return null;

        if (selection.Length > SelectionScanBytes)
            return null;

        return CountCharacters(source, selection.Start, selection.End);
    }

    /// <summary>
    /// Characters between two byte offsets, counted the way the rest of the viewer decodes: one
    /// per rune, and one U+FFFD per maximal invalid subpart, so the number matches what the rows
    /// actually draw.
    /// </summary>
    private static int CountCharacters(IByteSource source, long start, long end)
    {
        int count = 0;
        long at = start;
        Span<byte> carry = stackalloc byte[4];

        while (at < end)
        {
            var span = source.GetContiguousSpan(at, (int)Math.Min(end - at, int.MaxValue));
            if (span.IsEmpty)
                break;

            if (span.Length > end - at)
                span = span[..(int)(end - at)];

            // Almost every line in almost every file: one byte, one character. The check is
            // vectorized, so taking it costs a fraction of the per-rune decode it replaces.
            if (Ascii.IsValid(span))
            {
                count += span.Length;
                at += span.Length;
                continue;
            }

            int consumed = 0;
            while (consumed < span.Length)
            {
                var remaining = span[consumed..];

                // A sequence split across a piece boundary decodes short here, so the tail is
                // re-read through CopyTo, which crosses boundaries. Rare, and bounded by 4 bytes.
                if (remaining.Length < 4 && at + consumed + remaining.Length < end)
                {
                    int read = source.CopyTo(at + consumed, carry);
                    int usable = (int)Math.Min(read, end - (at + consumed));
                    Rune.DecodeFromUtf8(carry[..usable], out _, out int carriedBytes);
                    consumed += carriedBytes;
                    count++;
                    continue;
                }

                Rune.DecodeFromUtf8(remaining, out _, out int bytesConsumed);
                consumed += bytesConsumed;
                count++;
            }

            at += consumed;
        }

        return count;
    }
}
