using System;

namespace Argonaut.Features.Hex;

/// <summary>What a copy from the hex view puts on the clipboard.</summary>
public enum HexCopyForm
{
    /// <summary>The bytes as hex digits, a space between each: <c>89 50 4E 47</c>.</summary>
    Hex,

    /// <summary>The character column as drawn: printable ASCII as itself, or UTF-8 characters
    /// when the column reads UTF-8, and <c>.</c> for the rest.</summary>
    Text,

    /// <summary>The bytes themselves.</summary>
    Bytes,
}

/// <summary>Turns selected bytes into what a copy puts on the clipboard.</summary>
public static class HexCopy
{
    /// <summary>The most a copy takes. Selecting a whole multi-gigabyte document is free; turning
    /// it into a string is not, so a larger copy is refused rather than truncated. As hex text this
    /// is three characters a byte - 48 million of them.</summary>
    public const long MaxBytes = 16L * 1024 * 1024;

    public static string AsHex(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return string.Empty;

        return string.Create(bytes.Length * 3 - 1, bytes, static (text, values) =>
        {
            const string digits = "0123456789ABCDEF";
            for (int i = 0; i < values.Length; i++)
            {
                int at = i * 3;
                if (i > 0)
                    text[at - 1] = ' ';
                text[at] = digits[values[i] >> 4];
                text[at + 1] = digits[values[i] & 0xF];
            }
        });
    }

    public static string AsText(ReadOnlySpan<byte> bytes)
    {
        return string.Create(bytes.Length, bytes, static (text, values) =>
        {
            for (int i = 0; i < values.Length; i++)
                text[i] = HexRows.ClassOf(values[i]) == HexByteClass.Printable ? (char)values[i] : '.';
        });
    }
}
