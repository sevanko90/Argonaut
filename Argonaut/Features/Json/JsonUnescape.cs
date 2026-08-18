using System;
using System.Buffers;
using System.Text;

namespace Argonaut.Features.Json;

/// <summary>
/// Minimal JSON string unescaping over raw (already-validated) content bytes, for the diff's
/// property-name matching. The indexer's own hashing unescapes through the live
/// <see cref="System.Text.Json.Utf8JsonReader"/>; the differ runs long after parsing, holding
/// only (offset, length) spans, so it needs a reader-free equivalent. Input is always a span
/// the indexer recorded from a successful parse, so escapes are well-formed by construction -
/// malformed trailing escapes are copied through verbatim rather than rejected.
/// </summary>
internal static class JsonUnescape
{
    /// <summary>True when the raw bytes need no unescaping (the overwhelmingly common case).</summary>
    public static bool IsPlain(ReadOnlySpan<byte> raw) => raw.IndexOf((byte)'\\') < 0;

    /// <summary>
    /// Decodes <paramref name="raw"/> (JSON string content, quotes excluded) into
    /// <paramref name="dest"/>, returning the decoded byte count. Decoded output never
    /// exceeds the raw length (every escape sequence is at least as long as its decoded
    /// bytes), so sizing <paramref name="dest"/> to the raw length always suffices.
    /// </summary>
    public static int Unescape(ReadOnlySpan<byte> raw, Span<byte> dest)
    {
        int di = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            byte b = raw[i];
            if (b != (byte)'\\' || i + 1 >= raw.Length)
            {
                dest[di++] = b;
                continue;
            }

            byte esc = raw[++i];
            switch (esc)
            {
                case (byte)'"': dest[di++] = (byte)'"'; break;
                case (byte)'\\': dest[di++] = (byte)'\\'; break;
                case (byte)'/': dest[di++] = (byte)'/'; break;
                case (byte)'b': dest[di++] = (byte)'\b'; break;
                case (byte)'f': dest[di++] = (byte)'\f'; break;
                case (byte)'n': dest[di++] = (byte)'\n'; break;
                case (byte)'r': dest[di++] = (byte)'\r'; break;
                case (byte)'t': dest[di++] = (byte)'\t'; break;
                case (byte)'u':
                {
                    int code = ReadHex4(raw, i + 1);
                    i += 4;

                    // A high surrogate must be followed by an escaped low surrogate to form
                    // one code point; the parse validated this, so pairing is best-effort.
                    if (code is >= 0xD800 and <= 0xDBFF && i + 6 < raw.Length && raw[i + 1] == (byte)'\\' && raw[i + 2] == (byte)'u')
                    {
                        int low = ReadHex4(raw, i + 3);
                        if (low is >= 0xDC00 and <= 0xDFFF)
                        {
                            code = 0x10000 + ((code - 0xD800) << 10) + (low - 0xDC00);
                            i += 6;
                        }
                    }

                    var rune = Rune.IsValid(code) ? new Rune(code) : Rune.ReplacementChar;
                    di += rune.EncodeToUtf8(dest[di..]);
                    break;
                }
                default:
                    // Not a valid escape - can't happen for indexer-recorded spans; copy through.
                    dest[di++] = (byte)'\\';
                    dest[di++] = esc;
                    break;
            }
        }

        return di;
    }

    private static int ReadHex4(ReadOnlySpan<byte> raw, int start)
    {
        int value = 0;
        for (int k = 0; k < 4; k++)
        {
            int digit = raw[start + k] switch
            {
                >= (byte)'0' and <= (byte)'9' => raw[start + k] - '0',
                >= (byte)'a' and <= (byte)'f' => raw[start + k] - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => raw[start + k] - 'A' + 10,
                _ => 0
            };
            value = (value << 4) | digit;
        }

        return value;
    }

    /// <summary>Hash of the *decoded* form (matches <see cref="JsonContentHasher.HashDecodedString"/>
    /// for the same code points), so an escaped and a literal spelling of one name match.</summary>
    public static ulong DecodedHash(ReadOnlySpan<byte> raw)
    {
        if (IsPlain(raw))
            return JsonContentHasher.HashDecodedString(raw);

        byte[]? rented = null;
        Span<byte> buffer = raw.Length <= 256 ? stackalloc byte[256] : (rented = ArrayPool<byte>.Shared.Rent(raw.Length));
        int written = Unescape(raw, buffer);
        ulong hash = JsonContentHasher.HashDecodedString(buffer[..written]);
        if (rented is not null)
            ArrayPool<byte>.Shared.Return(rented);
        return hash;
    }

    /// <summary>Byte equality of the two spans' *decoded* forms - the cheap collision guard
    /// run only on hash-matched pairs.</summary>
    public static bool DecodedEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (IsPlain(a) && IsPlain(b))
            return a.SequenceEqual(b);

        byte[]? rentedA = null, rentedB = null;
        Span<byte> bufA = a.Length <= 256 ? stackalloc byte[256] : (rentedA = ArrayPool<byte>.Shared.Rent(a.Length));
        Span<byte> bufB = b.Length <= 256 ? stackalloc byte[256] : (rentedB = ArrayPool<byte>.Shared.Rent(b.Length));

        int lenA = Unescape(a, bufA);
        int lenB = Unescape(b, bufB);
        bool equal = bufA[..lenA].SequenceEqual(bufB[..lenB]);

        if (rentedA is not null)
            ArrayPool<byte>.Shared.Return(rentedA);
        if (rentedB is not null)
            ArrayPool<byte>.Shared.Return(rentedB);
        return equal;
    }
}
