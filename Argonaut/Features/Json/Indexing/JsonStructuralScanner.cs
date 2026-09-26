using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Argonaut.Engine.Bytes;

namespace Argonaut.Features.Json.Indexing;

/// <summary>What <see cref="JsonStructuralScanner.TrySkipValue"/> found.</summary>
public enum JsonSkipOutcome
{
    /// <summary>The value ended; its end offset was returned.</summary>
    Skipped,

    /// <summary>The value runs past the bytes available so far on a source that is still
    /// receiving. Ask again once more has arrived.</summary>
    Incomplete,

    /// <summary>The scanner cannot answer: a comment outside a string, a stray closing bracket,
    /// or the value runs past the end of a settled source. A full parse
    /// (<c>Utf8JsonReader.Skip</c>) gives the definitive answer, including the error.</summary>
    NeedsFullParse,
}

/// <summary>
/// Finds where a JSON value ends without tokenising it, at the speed of a memory scan. Every
/// 64-byte block is classified into bitmasks - quotes, backslashes, brackets, slashes - with
/// vector compares; string interiors are masked out by a prefix XOR over the unescaped quotes,
/// and a subtree is skipped by counting brackets over what remains (the simdjson stage-1
/// approach). A block is examined bit by bit only when the value could end inside it.
///
/// It does not validate. It trusts that the bytes are JSON and answers
/// <see cref="JsonSkipOutcome.NeedsFullParse"/> for the cases it cannot decide cheaply, so a
/// caller keeps <c>Utf8JsonReader</c> as the fallback and as the source of error messages.
/// Comments are one of those cases: the tree reads JSONC, but a comment can hide a bracket and a
/// masked scan cannot tell, so the first slash outside a string hands over.
///
/// Must be started at a value (or whitespace before one), never inside a string or token: the
/// masks carry string state forward from the start, and a checkpoint is a value start by
/// construction.
/// </summary>
public static class JsonStructuralScanner
{
    internal const int BlockSize = 64;

    /// <summary>Bytes asked of the source per read. Only affects how often a split or growing
    /// source is re-asked; a single-buffer source serves each request whole.</summary>
    private const int ReadStride = 64 * 1024;

    /// <summary>
    /// Finds the end of the value at or after <paramref name="offset"/> (leading whitespace is
    /// skipped). On <see cref="JsonSkipOutcome.Skipped"/>, <paramref name="end"/> is the offset
    /// one past the value's last byte - after a string's closing quote, after a container's
    /// closing bracket.
    /// </summary>
    public static JsonSkipOutcome TrySkipValue(IByteSource source, long offset, out long end)
    {
        end = offset;
        var start = SkipWhitespace(source, offset);
        if (start.Outcome != JsonSkipOutcome.Skipped)
            return start.Outcome;

        long valueStart = start.Offset;
        return source.ByteAt(valueStart) switch
        {
            (byte)'{' or (byte)'[' => ScanBlocks(source, valueStart, ValueShape.Container, out end),
            (byte)'"' => ScanBlocks(source, valueStart, ValueShape.String, out end),
            (byte)'/' => JsonSkipOutcome.NeedsFullParse,
            (byte)'}' or (byte)']' or (byte)',' or (byte)':' => JsonSkipOutcome.NeedsFullParse,
            _ => SkipBareScalar(source, valueStart, out end),
        };
    }

    private enum ValueShape
    {
        Container,
        String,
    }

    /// <summary>The string and escape state one block hands the next.</summary>
    internal struct CarryState
    {
        /// <summary>All ones when the previous block ended inside a string, else zero.</summary>
        public ulong InString;

        /// <summary>True when the previous block's last byte was a backslash that escapes this
        /// block's first byte.</summary>
        public bool EscapesNext;
    }

    /// <summary>One block's structure, bit i describing byte i. Everything but
    /// <see cref="Quote"/> is already restricted to bytes outside strings.</summary>
    internal struct BlockMasks
    {
        /// <summary><c>{</c> or <c>[</c> outside a string.</summary>
        public ulong Open;

        /// <summary><c>}</c> or <c>]</c> outside a string.</summary>
        public ulong Close;

        /// <summary><c>/</c> outside a string - the start of a comment, in valid JSONC.</summary>
        public ulong Slash;

        /// <summary>Unescaped quotes: each one opens or closes a string.</summary>
        public ulong Quote;
    }

    private static JsonSkipOutcome ScanBlocks(IByteSource source, long valueStart, ValueShape shape, out long end)
    {
        end = valueStart;
        var carry = default(CarryState);
        int depth = 0;
        int quotesSeen = 0;
        long blockOffset = valueStart;
        Span<byte> gathered = stackalloc byte[BlockSize];

        while (true)
        {
            var span = source.GetContiguousSpan(blockOffset, ReadStride);
            int whole = span.Length - (span.Length % BlockSize);
            for (int i = 0; i < whole; i += BlockSize)
            {
                Classify(span.Slice(i, BlockSize), ref carry, out var masks);
                if (Settle(masks, shape, blockOffset + i, ref depth, ref quotesSeen, out end) is { } settled)
                    return settled;
            }

            blockOffset += whole;
            if (whole == span.Length && !span.IsEmpty)
                continue;

            // A partial block: either the source's buffer ends here and the next one continues,
            // or this is the end of what has arrived. Gather across the boundary, and pad with
            // whitespace, which can neither open nor close anything.
            int copied = source.CopyTo(blockOffset, gathered);
            gathered.Slice(copied).Fill((byte)' ');
            Classify(gathered, ref carry, out var tail);
            if (Settle(tail, shape, blockOffset, ref depth, ref quotesSeen, out end) is { } finished)
                return finished;

            if (copied < BlockSize)
                return source.LengthSettled ? JsonSkipOutcome.NeedsFullParse : JsonSkipOutcome.Incomplete;

            blockOffset += BlockSize;
        }
    }

    /// <summary>Applies one block to the running count; returns an outcome once the value's end
    /// or a reason to stop has been found, else null to carry on.</summary>
    private static JsonSkipOutcome? Settle(in BlockMasks masks, ValueShape shape, long blockOffset,
        ref int depth, ref int quotesSeen, out long end)
    {
        end = 0;
        if (shape == ValueShape.String)
        {
            // The opening quote is the first quote seen; the value ends at the second.
            ulong quotes = masks.Quote;
            if (quotesSeen + BitOperations.PopCount(quotes) < 2)
            {
                quotesSeen += BitOperations.PopCount(quotes);
                return null;
            }

            if (quotesSeen == 0)
                quotes &= quotes - 1; // drop the opening quote
            end = blockOffset + BitOperations.TrailingZeroCount(quotes) + 1;
            return JsonSkipOutcome.Skipped;
        }

        ulong slash = masks.Slash;
        ulong brackets = masks.Open | masks.Close;
        if (slash != 0)
        {
            // A comment only matters if it comes before the value's end. Walk the brackets
            // below the first slash; if the value is still open there, hand over.
            ulong beforeSlash = (1UL << BitOperations.TrailingZeroCount(slash)) - 1;
            if (Walk(brackets & beforeSlash, masks.Open, blockOffset, ref depth, out end) is { } early)
                return early;

            return JsonSkipOutcome.NeedsFullParse;
        }

        int closes = BitOperations.PopCount(masks.Close);
        if (depth > closes)
        {
            // Cannot reach zero inside this block, so only the net change matters.
            depth += BitOperations.PopCount(masks.Open) - closes;
            return null;
        }

        return Walk(brackets, masks.Open, blockOffset, ref depth, out end);
    }

    private static JsonSkipOutcome? Walk(ulong brackets, ulong open, long blockOffset, ref int depth, out long end)
    {
        end = 0;
        while (brackets != 0)
        {
            int bit = BitOperations.TrailingZeroCount(brackets);
            brackets &= brackets - 1;
            if ((open & (1UL << bit)) != 0)
            {
                depth++;
                continue;
            }

            if (--depth == 0)
            {
                end = blockOffset + bit + 1;
                return JsonSkipOutcome.Skipped;
            }

            if (depth < 0)
                return JsonSkipOutcome.NeedsFullParse;
        }

        return null;
    }

    /// <summary>
    /// Classifies one 64-byte block. Brackets fold into two compares because <c>[</c>/<c>{</c>
    /// and <c>]</c>/<c>}</c> differ only in bit 0x20, and nothing else maps onto them when that
    /// bit is set.
    /// </summary>
    internal static void Classify(ReadOnlySpan<byte> block, ref CarryState carry, out BlockMasks masks)
    {
        ref byte first = ref MemoryMarshal.GetReference(block);
        var v0 = Vector128.LoadUnsafe(ref first, 0);
        var v1 = Vector128.LoadUnsafe(ref first, 16);
        var v2 = Vector128.LoadUnsafe(ref first, 32);
        var v3 = Vector128.LoadUnsafe(ref first, 48);

        ulong quote = Match(v0, v1, v2, v3, (byte)'"');
        ulong backslash = Match(v0, v1, v2, v3, (byte)'\\');
        ulong slash = Match(v0, v1, v2, v3, (byte)'/');

        var caseFold = Vector128.Create((byte)0x20);
        ulong open = Match(v0 | caseFold, v1 | caseFold, v2 | caseFold, v3 | caseFold, (byte)'{');
        ulong close = Match(v0 | caseFold, v1 | caseFold, v2 | caseFold, v3 | caseFold, (byte)'}');

        ulong escaped = Escaped(backslash, ref carry.EscapesNext);
        quote &= ~escaped;

        ulong inString = PrefixXor(quote) ^ carry.InString;
        carry.InString = (ulong)((long)inString >> 63);

        masks.Quote = quote;
        masks.Open = open & ~inString;
        masks.Close = close & ~inString;
        masks.Slash = slash & ~inString;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Match(Vector128<byte> v0, Vector128<byte> v1, Vector128<byte> v2, Vector128<byte> v3, byte value)
    {
        var target = Vector128.Create(value);
        return Vector128.Equals(v0, target).ExtractMostSignificantBits()
            | ((ulong)Vector128.Equals(v1, target).ExtractMostSignificantBits() << 16)
            | ((ulong)Vector128.Equals(v2, target).ExtractMostSignificantBits() << 32)
            | ((ulong)Vector128.Equals(v3, target).ExtractMostSignificantBits() << 48);
    }

    /// <summary>
    /// The bytes escaped by a backslash: in a run of backslashes each odd one escapes the byte
    /// after it. Walked a backslash at a time rather than with simdjson's carry-add trick because
    /// backslashes are rare in real documents and this is obviously right; a block with none
    /// costs one test.
    /// </summary>
    internal static ulong Escaped(ulong backslash, ref bool escapesNext)
    {
        ulong escaped = escapesNext ? 1UL : 0UL;
        escapesNext = false;

        ulong escaping = backslash & ~escaped;
        while (escaping != 0)
        {
            int bit = BitOperations.TrailingZeroCount(escaping);
            if (bit == 63)
            {
                escapesNext = true;
                break;
            }

            escaped |= 1UL << (bit + 1);
            escaping &= ~(3UL << bit);
        }

        return escaped;
    }

    /// <summary>Bit i becomes the XOR of bits 0..i: set inside a string (opening quote
    /// included, closing quote excluded) given one bit per unescaped quote.</summary>
    internal static ulong PrefixXor(ulong bits)
    {
        bits ^= bits << 1;
        bits ^= bits << 2;
        bits ^= bits << 4;
        bits ^= bits << 8;
        bits ^= bits << 16;
        bits ^= bits << 32;
        return bits;
    }

    private static (JsonSkipOutcome Outcome, long Offset) SkipWhitespace(IByteSource source, long offset)
    {
        while (true)
        {
            var span = source.GetContiguousSpan(offset, ReadStride);
            if (span.IsEmpty)
                return (source.LengthSettled ? JsonSkipOutcome.NeedsFullParse : JsonSkipOutcome.Incomplete, offset);

            int content = span.IndexOfAnyExcept(Whitespace);
            if (content >= 0)
                return (JsonSkipOutcome.Skipped, offset + content);

            offset += span.Length;
        }
    }

    /// <summary>A number, <c>true</c>, <c>false</c> or <c>null</c>: ends at the first delimiter.
    /// Only reached on short tokens, so a plain search is enough.</summary>
    private static JsonSkipOutcome SkipBareScalar(IByteSource source, long offset, out long end)
    {
        end = offset;
        while (true)
        {
            var span = source.GetContiguousSpan(end, ReadStride);
            if (span.IsEmpty)
            {
                // A bare scalar runs to the end of the document only when it is the whole
                // document; whether it has finished arriving is the source's call.
                return source.LengthSettled ? JsonSkipOutcome.Skipped : JsonSkipOutcome.Incomplete;
            }

            int delimiter = span.IndexOfAny(BareScalarDelimiters);
            if (delimiter >= 0)
            {
                end += delimiter;
                return JsonSkipOutcome.Skipped;
            }

            end += span.Length;
        }
    }

    private static readonly SearchValues<byte> Whitespace = SearchValues.Create(" \t\r\n"u8);

    private static readonly SearchValues<byte> BareScalarDelimiters =
        SearchValues.Create(" \t\r\n,:]}/"u8);
}
