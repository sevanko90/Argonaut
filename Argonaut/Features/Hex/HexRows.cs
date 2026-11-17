using System;

namespace Argonaut.Features.Hex;

/// <summary>How a byte is coloured in the hex view: its class, not its value.</summary>
public enum HexByteClass : byte
{
    /// <summary>0x00 - the padding and terminators that make up much of a binary file.</summary>
    Zero,

    /// <summary>Printable ASCII, 0x20-0x7E: what reads as text in the character column.</summary>
    Printable,

    /// <summary>Everything else: control bytes and bytes above 0x7F.</summary>
    Other,
}

/// <summary>
/// The hex view's geometry and text: 16 bytes a row, so every position is arithmetic on the offset -
/// row <c>r</c> starts at <c>r × 16</c>, the byte at <c>o</c> is on row <c>o / 16</c> - and no index
/// or scan is ever needed. Rows are <c>long</c>: a 40 GB file has 2.7 billion of them.
///
/// A row reads <c>OFFSET  XX XX XX XX XX XX XX XX  XX XX XX XX XX XX XX XX  ASCII...</c>: the offset
/// in hex, the bytes in two halves of eight, and the character column (printable ASCII as itself,
/// <c>.</c> for the rest). Bytes can be grouped (<see cref="GroupSizes"/>): a group's bytes are
/// written together, in file order, with a space after the group - <c>01020304 05060708</c> at
/// four - and the halves keep their extra space. Every size has a fixed width, so a row's
/// geometry is still arithmetic. Formatting writes into a buffer the caller owns, so drawing a
/// row allocates nothing for its text.
/// </summary>
public static class HexRows
{
    public const int BytesPerRow = 16;

    /// <summary>The fewest digits an offset is shown with, so small documents still read like a dump.</summary>
    public const int MinOffsetDigits = 8;

    private const string HexDigits = "0123456789ABCDEF";

    /// <summary>The sizes bytes can be grouped by. Each divides a row, and a half of one.</summary>
    public static readonly int[] GroupSizes = [1, 2, 4, 8];

    /// <summary>Whether <paramref name="size"/> is one of <see cref="GroupSizes"/>.</summary>
    public static bool IsGroupSize(int size) => size is 1 or 2 or 4 or 8;

    /// <summary>The first byte of the group holding <paramref name="offset"/>: groups start at
    /// multiples of their size from the start of the document.</summary>
    public static long GroupStart(long offset, int groupSize) => offset - offset % groupSize;

    /// <summary>Rows for a document of <paramref name="length"/> bytes; the last may be partial.</summary>
    public static long RowCount(long length) => length <= 0 ? 0 : (length + BytesPerRow - 1) / BytesPerRow;

    public static long RowOf(long offset) => offset / BytesPerRow;

    public static long StartOf(long row) => row * BytesPerRow;

    /// <summary>How many hex digits the offset column needs for a document of
    /// <paramref name="length"/> bytes - at least <see cref="MinOffsetDigits"/>.</summary>
    public static int OffsetDigits(long length)
    {
        int digits = 1;
        for (ulong last = (ulong)Math.Max(0, length - 1); last >= 16; last >>= 4)
            digits++;
        return Math.Max(MinOffsetDigits, digits);
    }

    /// <summary>Where the hex column starts in a row's text.</summary>
    public static int HexColumn(int offsetDigits) => offsetDigits + 2;

    /// <summary>Where byte <paramref name="index"/>'s two hex digits start: two characters a
    /// byte, a space after each group of <paramref name="groupSize"/>, and one more between the
    /// two halves of the row.</summary>
    public static int HexPosition(int offsetDigits, int index, int groupSize = 1)
        => HexColumn(offsetDigits) + index * 2 + index / groupSize + (index >= BytesPerRow / 2 ? 1 : 0);

    /// <summary>How many characters the hex column takes, from the first byte's digits to the
    /// last's.</summary>
    public static int HexWidth(int groupSize = 1) => HexPosition(0, BytesPerRow - 1, groupSize) - HexColumn(0) + 2;

    /// <summary>Where the character column starts: after the last byte's digits, two spaces.</summary>
    public static int CharacterColumn(int offsetDigits, int groupSize = 1) => HexColumn(offsetDigits) + HexWidth(groupSize) + 2;

    /// <summary>The length of a full row's text - the buffer <see cref="Format"/> needs.</summary>
    public static int LineLength(int offsetDigits, int groupSize = 1) => CharacterColumn(offsetDigits, groupSize) + BytesPerRow;

    public static HexByteClass ClassOf(byte value) => value switch
    {
        0 => HexByteClass.Zero,
        >= 0x20 and <= 0x7E => HexByteClass.Printable,
        _ => HexByteClass.Other,
    };

    /// <summary>
    /// Writes the row starting at <paramref name="rowStart"/> holding <paramref name="bytes"/> (16,
    /// or fewer on the last row) into <paramref name="line"/>, which must hold
    /// <see cref="LineLength"/> characters, with its bytes grouped by <paramref name="groupSize"/>.
    /// </summary>
    /// <returns>How many characters were written: a partial row's hex column is padded so its
    /// characters line up with the rows above, and the line ends after its last character.</returns>
    public static int Format(ReadOnlySpan<byte> bytes, long rowStart, int offsetDigits, Span<char> line, int groupSize = 1)
    {
        int length = CharacterColumn(offsetDigits, groupSize) + bytes.Length;
        line[..length].Fill(' ');

        ulong offset = (ulong)rowStart;
        for (int i = offsetDigits - 1; i >= 0; i--, offset >>= 4)
            line[i] = HexDigits[(int)(offset & 0xF)];

        int characters = CharacterColumn(offsetDigits, groupSize);
        for (int i = 0; i < bytes.Length; i++)
        {
            byte value = bytes[i];
            int at = HexPosition(offsetDigits, i, groupSize);
            line[at] = HexDigits[value >> 4];
            line[at + 1] = HexDigits[value & 0xF];
            line[characters + i] = ClassOf(value) == HexByteClass.Printable ? (char)value : '.';
        }

        return length;
    }
}
