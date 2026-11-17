using System;
using System.Buffers;
using System.Text;

namespace Argonaut.Features.Hex;

/// <summary>What a cell of the character column shows when it reads UTF-8.</summary>
public enum HexCellKind : byte
{
    /// <summary>The first byte of a character, which draws it - spreading over the cells of its
    /// other bytes in the same row.</summary>
    Character,

    /// <summary>A later byte of a character whose first byte is earlier in the same row: drawn
    /// over by that character, so empty.</summary>
    Continuation,

    /// <summary>A later byte of a character whose first byte ended the row above: a muted mark,
    /// so it reads as part of that character rather than as an unknown byte.</summary>
    CarriedOver,

    /// <summary>A byte no character can be read from, or a control: <c>.</c>, as in ASCII.</summary>
    Unreadable,
}

/// <summary>One cell of the character column read as UTF-8: what it is, and for the first byte of
/// a character, the character and how many bytes it takes.</summary>
public readonly record struct HexCell(HexCellKind Kind, Rune Character, int Length);

/// <summary>
/// Reads a row's bytes as UTF-8 for the character column. Every cell stays one byte: a character
/// is drawn once, at its first byte's cell, and the cells of its other bytes are its own to spread
/// over. A character crossing the row's end is clipped there, and its bytes on the next row show a
/// muted mark. To tell those apart, a row looks back at most three bytes before it starts, and
/// ahead at most three past its end - so the column stays arithmetic and needs no scan.
/// </summary>
public static class HexUtf8
{
    /// <summary>The most bytes a row looks before it starts, or past its end: a character's
    /// length less one.</summary>
    public const int Reach = 3;

    /// <summary>
    /// Reads the row's cells into <paramref name="cells"/>. <paramref name="window"/> holds up to
    /// <see cref="Reach"/> bytes before the row (<paramref name="before"/> of them), the row's
    /// <paramref name="count"/> bytes, and up to <see cref="Reach"/> after it.
    /// </summary>
    public static void ReadRow(ReadOnlySpan<byte> window, int before, int count, Span<HexCell> cells)
    {
        int end = before + count;
        int at = before;

        // A character whose first byte is in the row above: the nearest byte back that starts a
        // whole character reaching into this row.
        for (int back = 1; back <= before; back++)
        {
            int lead = before - back;
            if (Rune.DecodeFromUtf8(window[lead..], out _, out int length) == OperationStatus.Done && length > back)
            {
                for (at = before; at < Math.Min(end, lead + length); at++)
                    cells[at - before] = new(HexCellKind.CarriedOver, default, 0);
                break;
            }

            if (window[lead] < 0x80)
                break;
        }

        while (at < end)
        {
            if (Rune.DecodeFromUtf8(window[at..], out var character, out int length) == OperationStatus.Done && !Rune.IsControl(character))
            {
                cells[at - before] = new(HexCellKind.Character, character, length);
                for (int i = 1; i < length && at + i < end; i++)
                    cells[at - before + i] = new(HexCellKind.Continuation, character, 0);
                at += length;
            }
            else
            {
                cells[at - before] = new(HexCellKind.Unreadable, default, 1);
                at++;
            }
        }
    }

    /// <summary>
    /// The character the byte at <paramref name="index"/> of <paramref name="window"/> belongs to -
    /// starting there or up to <see cref="Reach"/> bytes before - or null when it is part of no
    /// whole character.
    /// </summary>
    public static Rune? CharacterAt(ReadOnlySpan<byte> window, int index)
    {
        for (int back = 0; back <= Math.Min(Reach, index); back++)
        {
            int lead = index - back;
            if (Rune.DecodeFromUtf8(window[lead..], out var character, out int length) == OperationStatus.Done && length > back)
                return character;
        }

        return null;
    }

    /// <summary>Bytes as the UTF-8 column shows them: characters as themselves, and <c>.</c> for
    /// a control or a byte no character can be read from - a character cut off at either end
    /// included.</summary>
    public static string AsText(ReadOnlySpan<byte> bytes)
    {
        var text = new StringBuilder(bytes.Length);
        int at = 0;
        while (at < bytes.Length)
        {
            if (Rune.DecodeFromUtf8(bytes[at..], out var character, out int length) == OperationStatus.Done && !Rune.IsControl(character))
            {
                text.Append(character.ToString());
                at += length;
            }
            else
            {
                text.Append('.');
                at++;
            }
        }

        return text.ToString();
    }
}
