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
/// the caret already is. The character under the caret costs one decode of at most four bytes.
/// The column is a character count from the start of the line, which is a walk, so it is capped
/// (<see cref="ColumnScanBytes"/>) and reported as null past that rather than decoding backwards
/// through a multi-GB single-line document. A character count for the selection is capped the
/// same way (<see cref="SelectionScanBytes"/>).
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
    /// How far back the walk to the start of the line may go. A line here is not bounded - one
    /// minified JSON document is a single line of several GB - so the column is a question with
    /// no cheap answer on the pathological case, and answering "not from here" beats freezing.
    /// </summary>
    public const int ColumnScanBytes = 64 * 1024;

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

        long offset = Math.Clamp(caret.Offset, 0, source.Length);
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
        if (offset >= source.Length)
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
    /// The line number comes from the row index, which only carries one on the row that starts a
    /// line - continuation rows report null - so the walk goes back through the soft-wrapped rows
    /// to find it. Both the walk and the decode that follows it are capped: past
    /// <see cref="ColumnScanBytes"/> the column is null and the gutter says so.
    /// </summary>
    private static (int? LineNumber, int? Column) LineAndColumn(
        IRawRowIndex rows, IByteSource source, long offset)
    {
        if (rows.RowForOffset(offset) is not int rowIndex)
            return (null, null);

        var info = rows.GetRowInfo(rowIndex);
        while (info.LineNumber is null && rowIndex > 0)
        {
            if (offset - info.Start > ColumnScanBytes)
                return (null, null);

            info = rows.GetRowInfo(--rowIndex);
        }

        if (info.LineNumber is not int lineNumber)
            return (null, null);

        long lineStart = info.Start;
        if (offset - lineStart > ColumnScanBytes)
            return (lineNumber, null);

        return (lineNumber, 1 + CountCharacters(source, lineStart, offset));
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
