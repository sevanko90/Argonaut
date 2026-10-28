using System;
using System.Text;

namespace Argonaut.Features.Xml.Tree;

/// <summary>
/// Decodes the references in XML text: the five predefined entities and numeric character
/// references, in UTF-8 bytes. A reference that is not well-formed or names no valid character is
/// left as written. A decoded character is never longer than its reference, so the output fits a
/// buffer as long as the input.
/// </summary>
public static class XmlUnescape
{
    /// <summary><paramref name="source"/> with its references decoded. Returns
    /// <paramref name="source"/> itself when it holds no <c>&amp;</c>, which is nearly always;
    /// otherwise the decoded bytes are written to <paramref name="buffer"/>, which must be at
    /// least as long as the source.</summary>
    public static ReadOnlySpan<byte> Decode(ReadOnlySpan<byte> source, Span<byte> buffer)
    {
        int first = source.IndexOf((byte)'&');
        if (first < 0)
            return source;

        source[..first].CopyTo(buffer);
        int length = first;
        int i = first;
        while (i < source.Length)
        {
            if (source[i] != (byte)'&')
            {
                buffer[length++] = source[i++];
                continue;
            }

            int semicolon = source[i..].IndexOf((byte)';');
            if (semicolon > 1 && semicolon <= 10 && TryDecodeReference(source.Slice(i + 1, semicolon - 1), buffer[length..], out int written))
            {
                length += written;
                i += semicolon + 1;
            }
            else
            {
                buffer[length++] = source[i++];
            }
        }

        return buffer[..length];
    }

    private static bool TryDecodeReference(ReadOnlySpan<byte> name, Span<byte> destination, out int written)
    {
        written = 1;
        if (name.SequenceEqual("amp"u8)) { destination[0] = (byte)'&'; return true; }
        if (name.SequenceEqual("lt"u8)) { destination[0] = (byte)'<'; return true; }
        if (name.SequenceEqual("gt"u8)) { destination[0] = (byte)'>'; return true; }
        if (name.SequenceEqual("quot"u8)) { destination[0] = (byte)'"'; return true; }
        if (name.SequenceEqual("apos"u8)) { destination[0] = (byte)'\''; return true; }

        if (name[0] != (byte)'#' || name.Length < 2)
            return false;

        bool hex = name[1] == (byte)'x';
        var digits = name[(hex ? 2 : 1)..];
        if (digits.IsEmpty)
            return false;

        int codePoint = 0;
        foreach (byte d in digits)
        {
            int digit = d switch
            {
                >= (byte)'0' and <= (byte)'9' => d - '0',
                >= (byte)'a' and <= (byte)'f' when hex => d - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' when hex => d - 'A' + 10,
                _ => -1,
            };
            if (digit < 0)
                return false;

            codePoint = codePoint * (hex ? 16 : 10) + digit;
            if (codePoint > 0x10FFFF)
                return false;
        }

        if (codePoint == 0 || !Rune.TryCreate(codePoint, out var rune))
            return false;

        return rune.TryEncodeToUtf8(destination, out written);
    }
}
