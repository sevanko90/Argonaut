using System;
using System.Buffers;
using System.Text;

namespace Argonaut.Engine.Recipes;

/// <summary>
/// The byte-level escapes recipes apply, both ways. Each works on UTF-8 bytes and is
/// stateless across calls, so a whole-text run can feed it one contiguous run at a time; the
/// escape forms copy unescaped stretches with a single vectorised search rather than byte by byte.
/// </summary>
internal static class TextEscaping
{
    private static readonly SearchValues<byte> JsonNeedsEscape = SearchValues.Create(JsonEscapedBytes());

    private static readonly SearchValues<byte> UrlUnreserved =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~"u8);

    private static byte[] JsonEscapedBytes()
    {
        var bytes = new byte[0x22];
        for (int i = 0; i < 0x20; i++)
            bytes[i] = (byte)i;

        bytes[0x20] = (byte)'"';
        bytes[0x21] = (byte)'\\';
        return bytes;
    }

    public static void Escape(TextEscape escape, ReadOnlySpan<byte> text, IBufferWriter<byte> output)
    {
        switch (escape)
        {
            case TextEscape.Sql:
                Double(text, (byte)'\'', output);
                break;
            case TextEscape.Csv:
                Double(text, (byte)'"', output);
                break;
            case TextEscape.Json:
                JsonEscape(text, output);
                break;
            default:
                output.Write(text);
                break;
        }
    }

    public static void Unescape(TextEscape escape, ReadOnlySpan<byte> text, IBufferWriter<byte> output)
    {
        switch (escape)
        {
            case TextEscape.Sql:
                Undouble(text, (byte)'\'', output);
                break;
            case TextEscape.Csv:
                Undouble(text, (byte)'"', output);
                break;
            case TextEscape.Json:
                JsonUnescape(text, output);
                break;
            default:
                output.Write(text);
                break;
        }
    }

    private static void Double(ReadOnlySpan<byte> text, byte quote, IBufferWriter<byte> output)
    {
        int at;
        while ((at = text.IndexOf(quote)) >= 0)
        {
            output.Write(text[..(at + 1)]);
            output.Write([quote]);
            text = text[(at + 1)..];
        }

        output.Write(text);
    }

    /// <summary>Lenient: a lone quote is kept as it is rather than refused, since the reversed
    /// template has already decided where the item ends.</summary>
    private static void Undouble(ReadOnlySpan<byte> text, byte quote, IBufferWriter<byte> output)
    {
        int at;
        while ((at = text.IndexOf(quote)) >= 0)
        {
            output.Write(text[..(at + 1)]);
            bool doubled = at + 1 < text.Length && text[at + 1] == quote;
            text = text[(at + (doubled ? 2 : 1))..];
        }

        output.Write(text);
    }

    public static void JsonEscape(ReadOnlySpan<byte> text, IBufferWriter<byte> output)
    {
        Span<byte> unicode = stackalloc byte[6];
        "\\u00"u8.CopyTo(unicode);
        int at;
        while ((at = text.IndexOfAny(JsonNeedsEscape)) >= 0)
        {
            output.Write(text[..at]);
            byte b = text[at];
            switch (b)
            {
                case (byte)'"': output.Write("\\\""u8); break;
                case (byte)'\\': output.Write("\\\\"u8); break;
                case (byte)'\n': output.Write("\\n"u8); break;
                case (byte)'\r': output.Write("\\r"u8); break;
                case (byte)'\t': output.Write("\\t"u8); break;
                case (byte)'\b': output.Write("\\b"u8); break;
                case (byte)'\f': output.Write("\\f"u8); break;
                default:
                    unicode[4] = HexDigit(b >> 4);
                    unicode[5] = HexDigit(b & 0xF);
                    output.Write(unicode);
                    break;
            }

            text = text[(at + 1)..];
        }

        output.Write(text);
    }

    /// <summary>Decodes a JSON string body. A lone surrogate becomes U+FFFD rather than an
    /// error, as <c>System.Text.Json</c> reads it; an unknown or truncated escape is an error.</summary>
    public static void JsonUnescape(ReadOnlySpan<byte> text, IBufferWriter<byte> output)
    {
        int at;
        while ((at = text.IndexOf((byte)'\\')) >= 0)
        {
            output.Write(text[..at]);
            if (at + 1 >= text.Length)
                throw new RecipeException("ends with an incomplete escape '\\'");

            byte kind = text[at + 1];
            int consumed = 2;
            switch (kind)
            {
                case (byte)'"': output.Write("\""u8); break;
                case (byte)'\\': output.Write("\\"u8); break;
                case (byte)'/': output.Write("/"u8); break;
                case (byte)'n': output.Write("\n"u8); break;
                case (byte)'r': output.Write("\r"u8); break;
                case (byte)'t': output.Write("\t"u8); break;
                case (byte)'b': output.Write("\b"u8); break;
                case (byte)'f': output.Write("\f"u8); break;
                case (byte)'u':
                    consumed = WriteUnicodeEscape(text[at..], output);
                    break;
                default:
                    throw new RecipeException($"has an unknown escape '\\{(char)kind}'");
            }

            text = text[(at + consumed)..];
        }

        output.Write(text);
    }

    /// <summary>Writes the code point a <c>\uXXXX</c> (or a surrogate pair of them) at the start
    /// of <paramref name="text"/> stands for, and returns how many bytes it took.</summary>
    private static int WriteUnicodeEscape(ReadOnlySpan<byte> text, IBufferWriter<byte> output)
    {
        int first = ReadHex4(text);
        int consumed = 6;
        Rune rune;
        if (char.IsHighSurrogate((char)first) && text.Length >= 12 && text[6] == (byte)'\\' && text[7] == (byte)'u'
            && char.IsLowSurrogate((char)ReadHex4(text[6..])))
        {
            rune = new Rune((char)first, (char)ReadHex4(text[6..]));
            consumed = 12;
        }
        else
        {
            rune = Rune.IsValid(first) ? new Rune(first) : Rune.ReplacementChar;
        }

        Span<byte> utf8 = stackalloc byte[4];
        output.Write(utf8[..rune.EncodeToUtf8(utf8)]);
        return consumed;
    }

    private static int ReadHex4(ReadOnlySpan<byte> escape)
    {
        if (escape.Length < 6)
            throw new RecipeException("ends with an incomplete escape '\\u'");

        int code = 0;
        for (int i = 2; i < 6; i++)
        {
            int digit = HexValue(escape[i]);
            if (digit < 0)
                throw new RecipeException($"has an invalid escape '{Encoding.UTF8.GetString(escape[..6])}'");

            code = (code << 4) | digit;
        }

        return code;
    }

    public static void UrlEncode(ReadOnlySpan<byte> text, IBufferWriter<byte> output)
    {
        int at;
        Span<byte> percent = stackalloc byte[3];
        percent[0] = (byte)'%';
        while ((at = text.IndexOfAnyExcept(UrlUnreserved)) >= 0)
        {
            output.Write(text[..at]);
            percent[1] = HexDigit(text[at] >> 4);
            percent[2] = HexDigit(text[at] & 0xF);
            output.Write(percent);
            text = text[(at + 1)..];
        }

        output.Write(text);
    }

    /// <summary>Decodes <c>%XX</c> sequences. A <c>+</c> stays a <c>+</c>: that it means a space
    /// is a rule of HTML form bodies, not of URLs.</summary>
    public static void UrlDecode(ReadOnlySpan<byte> text, IBufferWriter<byte> output)
    {
        int at;
        while ((at = text.IndexOf((byte)'%')) >= 0)
        {
            output.Write(text[..at]);
            int high = at + 2 < text.Length ? HexValue(text[at + 1]) : -1;
            int low = high >= 0 ? HexValue(text[at + 2]) : -1;
            if (low < 0)
                throw new RecipeException("has a '%' that is not followed by two hex digits");

            output.Write([(byte)((high << 4) | low)]);
            text = text[(at + 3)..];
        }

        output.Write(text);
    }

    private static byte HexDigit(int nibble) => (byte)(nibble < 10 ? '0' + nibble : 'A' + nibble - 10);

    private static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };
}
