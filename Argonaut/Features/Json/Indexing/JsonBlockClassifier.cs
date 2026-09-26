using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Argonaut.Features.Json.Indexing;

/// <summary>One 64-byte block's structure, bit i describing byte i. Brackets and commas are
/// already restricted to bytes outside strings and comments.</summary>
internal struct JsonBlockMasks
{
    /// <summary><c>{</c> or <c>[</c>.</summary>
    public ulong Open;

    /// <summary><c>}</c> or <c>]</c>.</summary>
    public ulong Close;

    /// <summary><c>,</c>.</summary>
    public ulong Comma;

    /// <summary>Unescaped quotes outside comments: each one opens or closes a string.</summary>
    public ulong Quote;

    /// <summary>Anything that is not whitespace and not inside a comment - string contents
    /// included. Bytes up to 0x20 count as whitespace: outside a string only the four JSON
    /// whitespace characters are legal there, and inside one the quotes already mark it.</summary>
    public ulong Content;
}

/// <summary>
/// Classifies a JSON document 64 bytes at a time into <see cref="JsonBlockMasks"/>, carrying
/// string, escape and comment state from each block to the next. Start it at a value or between
/// values, never inside a string or comment.
///
/// Two paths produce the same masks. The vector path compares each block against the handful of
/// bytes that matter and masks string interiors out with a prefix XOR over the unescaped quotes
/// (the simdjson stage-1 approach). It cannot see comments - a bracket inside one would count -
/// so the first <c>/</c> outside a string switches the classifier to the scalar path for that
/// block and every block after: a byte-at-a-time state machine that reads comment bytes as
/// whitespace. A document either has comments or it does not, so there is no switching back.
/// </summary>
internal struct JsonBlockClassifier
{
    public const int BlockSize = 64;

    // All ones while inside a string at the end of the previous block, else zero - the form the
    // vector path XORs in. The scalar path keeps the same field as a flag.
    private ulong inString;
    private bool escapesNext;
    private bool commentAware;
    private bool inLineComment;
    private bool inBlockComment;
    private bool slashPending;
    private bool starPending;

    /// <summary>True once a comment has switched this classifier to the scalar path.</summary>
    public readonly bool IsCommentAware => commentAware;

    /// <summary>A classifier that uses the scalar path from the start. For tests that hold the
    /// two paths to the same answer.</summary>
    internal static JsonBlockClassifier CommentAware() => new() { commentAware = true };

    /// <summary>Classifies the next block, which must be exactly <see cref="BlockSize"/> bytes -
    /// pad a document's last partial block with spaces.</summary>
    public void Classify(ReadOnlySpan<byte> block, out JsonBlockMasks masks)
    {
        if (!commentAware)
        {
            var before = this;
            if (ClassifyVector(block, out masks))
                return;

            this = before;
            commentAware = true;
        }

        ClassifyScalar(block, out masks);
    }

    /// <summary>The vector path; false, with the carry state already advanced and to be
    /// discarded, when the block has a slash outside a string.</summary>
    private bool ClassifyVector(ReadOnlySpan<byte> block, out JsonBlockMasks masks)
    {
        ref byte first = ref MemoryMarshal.GetReference(block);
        var v0 = Vector128.LoadUnsafe(ref first, 0);
        var v1 = Vector128.LoadUnsafe(ref first, 16);
        var v2 = Vector128.LoadUnsafe(ref first, 32);
        var v3 = Vector128.LoadUnsafe(ref first, 48);

        ulong quote = Match(v0, v1, v2, v3, (byte)'"');
        ulong backslash = Match(v0, v1, v2, v3, (byte)'\\');
        ulong slash = Match(v0, v1, v2, v3, (byte)'/');
        ulong comma = Match(v0, v1, v2, v3, (byte)',');

        // [ and { (and ] and }) differ only in bit 0x20, and nothing else maps onto them when
        // that bit is set, so one compare finds each pair.
        var caseFold = Vector128.Create((byte)0x20);
        var f0 = v0 | caseFold;
        var f1 = v1 | caseFold;
        var f2 = v2 | caseFold;
        var f3 = v3 | caseFold;
        ulong open = Match(f0, f1, f2, f3, (byte)'{');
        ulong close = Match(f0, f1, f2, f3, (byte)'}');

        var space = Vector128.Create((byte)0x20);
        ulong blank = Vector128.LessThanOrEqual(v0, space).ExtractMostSignificantBits()
            | ((ulong)Vector128.LessThanOrEqual(v1, space).ExtractMostSignificantBits() << 16)
            | ((ulong)Vector128.LessThanOrEqual(v2, space).ExtractMostSignificantBits() << 32)
            | ((ulong)Vector128.LessThanOrEqual(v3, space).ExtractMostSignificantBits() << 48);

        ulong escaped = Escaped(backslash, ref escapesNext);
        quote &= ~escaped;

        ulong insideString = PrefixXor(quote) ^ inString;
        inString = (ulong)((long)insideString >> 63);

        masks = default;
        if ((slash & ~insideString) != 0)
            return false;

        masks.Quote = quote;
        masks.Open = open & ~insideString;
        masks.Close = close & ~insideString;
        masks.Comma = comma & ~insideString;
        masks.Content = ~blank;
        return true;
    }

    private void ClassifyScalar(ReadOnlySpan<byte> block, out JsonBlockMasks masks)
    {
        masks = default;
        bool insideString = inString != 0;

        for (int i = 0; i < BlockSize; i++)
        {
            byte b = block[i];
            ulong bit = 1UL << i;

            if (inLineComment)
            {
                if (b == (byte)'\n')
                    inLineComment = false;
                continue;
            }

            if (inBlockComment)
            {
                if (starPending && b == (byte)'/')
                {
                    inBlockComment = false;
                    starPending = false;
                }
                else
                {
                    starPending = b == (byte)'*';
                }

                continue;
            }

            if (insideString)
            {
                if (b > 0x20)
                    masks.Content |= bit;
                if (escapesNext)
                    escapesNext = false;
                else if (b == (byte)'\\')
                    escapesNext = true;
                else if (b == (byte)'"')
                {
                    insideString = false;
                    masks.Quote |= bit;
                }

                continue;
            }

            if (slashPending)
            {
                slashPending = false;
                if (b == (byte)'/')
                {
                    inLineComment = true;
                    continue;
                }

                if (b == (byte)'*')
                {
                    inBlockComment = true;
                    starPending = false;
                    continue;
                }

                // A lone slash is not JSON; leave it to validation and read on.
            }

            switch (b)
            {
                case (byte)'/':
                    slashPending = true;
                    break;
                case (byte)'"':
                    insideString = true;
                    masks.Quote |= bit;
                    masks.Content |= bit;
                    break;
                case (byte)'{' or (byte)'[':
                    masks.Open |= bit;
                    masks.Content |= bit;
                    break;
                case (byte)'}' or (byte)']':
                    masks.Close |= bit;
                    masks.Content |= bit;
                    break;
                case (byte)',':
                    masks.Comma |= bit;
                    masks.Content |= bit;
                    break;
                default:
                    if (b > 0x20)
                        masks.Content |= bit;
                    break;
            }
        }

        inString = insideString ? ulong.MaxValue : 0;
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
}
