using System;

namespace Argonaut.Features.Xml.Tree;

/// <summary>
/// XML text in the form the hint providers read: a string's content as a JSON file holds it, with
/// quotes, backslashes and control characters escaped. The providers were written for JSON strings
/// - JSON embedded in a string is recognised by its <c>\"</c> - so text that holds those
/// characters is escaped to match, after its references are decoded. Nearly all text holds none,
/// and is passed through untouched.
/// </summary>
public static class XmlHintText
{
    /// <summary>The bytes of <see cref="Escaped"/> for <paramref name="text"/>: its own length when
    /// nothing needs escaping.</summary>
    public static int EscapedLength(ReadOnlySpan<byte> text)
    {
        int length = text.Length;
        foreach (byte b in text)
        {
            if (b is (byte)'"' or (byte)'\\' or (byte)'\n' or (byte)'\r' or (byte)'\t')
                length += 1;
            else if (b < 0x20)
                length += 5;
        }

        return length;
    }

    /// <summary>
    /// <paramref name="text"/> escaped as a JSON string's content, written to
    /// <paramref name="buffer"/> (at least <see cref="EscapedLength"/> long) - or
    /// <paramref name="text"/> itself when nothing needs escaping.
    /// </summary>
    public static ReadOnlySpan<byte> Escaped(ReadOnlySpan<byte> text, Span<byte> buffer)
    {
        int length = 0;
        bool changed = false;
        foreach (byte b in text)
        {
            switch (b)
            {
                case (byte)'"' or (byte)'\\':
                    buffer[length++] = (byte)'\\';
                    buffer[length++] = b;
                    changed = true;
                    break;
                case (byte)'\n':
                    buffer[length++] = (byte)'\\';
                    buffer[length++] = (byte)'n';
                    changed = true;
                    break;
                case (byte)'\r':
                    buffer[length++] = (byte)'\\';
                    buffer[length++] = (byte)'r';
                    changed = true;
                    break;
                case (byte)'\t':
                    buffer[length++] = (byte)'\\';
                    buffer[length++] = (byte)'t';
                    changed = true;
                    break;
                case < 0x20:
                    "\\u00"u8.CopyTo(buffer[length..]);
                    length += 4;
                    buffer[length++] = (byte)"0123456789abcdef"[b >> 4];
                    buffer[length++] = (byte)"0123456789abcdef"[b & 0xF];
                    changed = true;
                    break;
                default:
                    buffer[length++] = b;
                    break;
            }
        }

        return changed ? buffer[..length] : text;
    }

    /// <summary>The text as written, as the hint providers read it: its references decoded, then
    /// escaped. <paramref name="fits"/> is false, and the result empty, when the escaped text would
    /// not fit <paramref name="escapeBuffer"/> - a pathological value, which is simply not hinted.</summary>
    public static ReadOnlySpan<byte> ForHints(ReadOnlySpan<byte> written, Span<byte> decodeBuffer, Span<byte> escapeBuffer, out bool fits)
    {
        var decoded = XmlUnescape.Decode(written, decodeBuffer);
        fits = EscapedLength(decoded) <= escapeBuffer.Length;
        return fits ? Escaped(decoded, escapeBuffer) : default;
    }
}
