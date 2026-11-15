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
/// in hex, the bytes in two groups of eight, and the character column (printable ASCII as itself,
/// <c>.</c> for the rest). Formatting writes into a buffer the caller owns, so drawing a row
/// allocates nothing for its text.
/// </summary>
public static class HexRows
{
    public const int BytesPerRow = 16;

    /// <summary>The fewest digits an offset is shown with, so small documents still read like a dump.</summary>
    public const int MinOffsetDigits = 8;

    private const string HexDigits = "0123456789ABCDEF";

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

    /// <summary>Where byte <paramref name="index"/>'s two hex digits start: three characters a
    /// byte, and one more space between the two groups of eight.</summary>
    public static int HexPosition(int offsetDigits, int index)
        => HexColumn(offsetDigits) + index * 3 + (index >= BytesPerRow / 2 ? 1 : 0);

    /// <summary>Where the character column starts: after the last byte's digits, two spaces.</summary>
    public static int CharacterColumn(int offsetDigits) => HexColumn(offsetDigits) + BytesPerRow * 3 + 2;

    /// <summary>The length of a full row's text - the buffer <see cref="Format"/> needs.</summary>
    public static int LineLength(int offsetDigits) => CharacterColumn(offsetDigits) + BytesPerRow;

    public static HexByteClass ClassOf(byte value) => value switch
    {
        0 => HexByteClass.Zero,
        >= 0x20 and <= 0x7E => HexByteClass.Printable,
        _ => HexByteClass.Other,
    };

    /// <summary>
    /// Writes the row starting at <paramref name="rowStart"/> holding <paramref name="bytes"/> (16,
    /// or fewer on the last row) into <paramref name="line"/>, which must hold
    /// <see cref="LineLength"/> characters.
    /// </summary>
    /// <returns>How many characters were written: a partial row's hex column is padded so its
    /// characters line up with the rows above, and the line ends after its last character.</returns>
    public static int Format(ReadOnlySpan<byte> bytes, long rowStart, int offsetDigits, Span<char> line)
    {
        int length = CharacterColumn(offsetDigits) + bytes.Length;
        line[..length].Fill(' ');

        ulong offset = (ulong)rowStart;
        for (int i = offsetDigits - 1; i >= 0; i--, offset >>= 4)
            line[i] = HexDigits[(int)(offset & 0xF)];

        int characters = CharacterColumn(offsetDigits);
        for (int i = 0; i < bytes.Length; i++)
        {
            byte value = bytes[i];
            int at = HexPosition(offsetDigits, i);
            line[at] = HexDigits[value >> 4];
            line[at + 1] = HexDigits[value & 0xF];
            line[characters + i] = ClassOf(value) == HexByteClass.Printable ? (char)value : '.';
        }

        return length;
    }
}
