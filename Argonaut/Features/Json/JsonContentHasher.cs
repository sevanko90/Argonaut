using System;
using System.Buffers;
using System.IO.Hashing;
using System.Text.Json;

namespace Argonaut.Features.Json;

/// <summary>
/// The pure hashing rules behind <see cref="JsonStructureIndex"/>'s optional per-token
/// content hashes (see <see cref="JsonIndexOptions.ComputeContentHashes"/>). A node's hash
/// covers its content and nothing else - not its own key, not its path, not its parent -
/// so a subtree's hash is invariant under relocation; see the diff plan for why that
/// invariant is load-bearing.
///
/// Semantic-equality promises (each backed by a test):
///
///  - Strings hash their *decoded* code points: <c>"café"</c> written literally and via
///    <c>é</c> escapes hash equal. No Unicode canonical normalization beyond JSON's
///    own escaping - é and e+combining-accent stay distinct, matching JSON string equality.
///  - Numbers hash a canonical form: <c>1</c>, <c>1.0</c>, <c>1e0</c> and <c>100e-2</c>
///    hash equal. The canonical form is the plain integer spelling when the value is an
///    integer whose digits fit <see cref="MaxCanonicalNumberLength"/>, otherwise normalized
///    scientific notation <c>d[.ddd]e&lt;exp&gt;</c> (significand stripped of leading/trailing
///    zeros, one digit before the point). <c>-0</c> canonicalizes to <c>0</c>. Numbers too
///    large for the canonical buffer (hundreds of digits) fall back to hashing their raw
///    bytes - two *different spellings* of such a value may then compare unequal (a false
///    "changed", never a false "unchanged").
///  - Kinds never collide by construction: <c>"1"</c> (string) and <c>1</c> (number) hash
///    with different seeds, containers with per-kind salts.
///  - Objects combine members commutatively (a multiset hash of mix(nameHash, valueHash)),
///    so property order is irrelevant; arrays combine sequentially, so element order matters.
///
/// The fast path hashes raw bytes straight off the mapping with no allocation; the slow
/// path (escaped strings, non-integer numbers) normalizes into a stackalloc/pooled buffer,
/// still allocation-free. On typical documents &gt;95% of scalars take the fast path.
/// </summary>
internal static class JsonContentHasher
{
    // Per-kind seeds/salts: what keeps "1" (string) and 1 (number), and {} and [], apart.
    private const long StringSeed = unchecked((long)0x9E3779B97F4A7C15UL);
    private const long NumberSeed = unchecked((long)0xC2B2AE3D27D4EB4FUL);
    private const ulong ObjectSalt = 0xA0761D6478BD642FUL;
    private const ulong ArraySalt = 0xE7037ED1A0B428DBUL;

    public static readonly ulong TrueHash = Avalanche(0x0074727565UL);
    public static readonly ulong FalseHash = Avalanche(0x66616C7365UL);
    public static readonly ulong NullHash = Avalanche(0x006E756C6CUL);

    /// <summary>Longest number the canonicalizer will spell out; beyond this the raw bytes
    /// are hashed instead (see class remarks for the consequence).</summary>
    internal const int MaxCanonicalNumberLength = 512;

    // Escaped strings are unescaped via Utf8JsonReader.CopyString; values short enough go
    // through a stackalloc buffer, longer ones through the shared pool. Unescaping never
    // grows a string (every escape sequence is at least as long as its decoded bytes), so
    // the raw length always bounds the buffer.
    private const int StackStringBufferLength = 256;

    /// <summary>
    /// Hashes the string/property-name token the reader is currently positioned on.
    /// <paramref name="rawSpan"/> is the token's content bytes (quotes excluded) straight
    /// off the mapping; the reader is only consulted on the slow (escaped) path, where
    /// <see cref="Utf8JsonReader.CopyString(Span{byte})"/> does the unescaping.
    /// </summary>
    public static ulong HashStringToken(ref Utf8JsonReader reader, ReadOnlySpan<byte> rawSpan)
    {
        if (rawSpan.IndexOf((byte)'\\') < 0)
            return XxHash3.HashToUInt64(rawSpan, StringSeed);

        byte[]? rented = null;
        Span<byte> buffer = rawSpan.Length <= StackStringBufferLength
            ? stackalloc byte[StackStringBufferLength]
            : (rented = ArrayPool<byte>.Shared.Rent(rawSpan.Length));

        int written = reader.CopyString(buffer);
        ulong hash = XxHash3.HashToUInt64(buffer[..written], StringSeed);

        if (rented is not null)
            ArrayPool<byte>.Shared.Return(rented);

        return hash;
    }

    /// <summary>Hashes an already-decoded (unescaped) UTF-8 string. Same result as
    /// <see cref="HashStringToken"/> for the same decoded bytes - used by tests and by any
    /// caller that has the decoded form in hand.</summary>
    public static ulong HashDecodedString(ReadOnlySpan<byte> decodedUtf8)
        => XxHash3.HashToUInt64(decodedUtf8, StringSeed);

    public static ulong HashNumber(ReadOnlySpan<byte> raw)
    {
        // Fast path: a plain integer's raw bytes already ARE the canonical form (no
        // leading zeros - JSON forbids them - no fraction, no exponent). "-0" is the one
        // integer spelling that isn't canonical.
        bool slow = raw.IndexOfAny((byte)'.', (byte)'e', (byte)'E') >= 0
            || (raw.Length >= 2 && raw[0] == (byte)'-' && raw[1] == (byte)'0');
        if (!slow)
            return XxHash3.HashToUInt64(raw, NumberSeed);

        Span<byte> buffer = stackalloc byte[MaxCanonicalNumberLength];
        int length = CanonicalizeNumber(raw, buffer);
        return length >= 0
            ? XxHash3.HashToUInt64(buffer[..length], NumberSeed)
            : XxHash3.HashToUInt64(raw, NumberSeed);
    }

    /// <summary>
    /// Rewrites a syntactically valid JSON number into its canonical spelling (see class
    /// remarks). Returns the canonical byte count, or -1 when it doesn't fit
    /// <paramref name="dest"/> (caller falls back to raw-byte hashing).
    /// </summary>
    internal static int CanonicalizeNumber(ReadOnlySpan<byte> raw, Span<byte> dest)
    {
        int i = 0;
        bool negative = raw[0] == (byte)'-';
        if (negative)
            i++;

        int intStart = i;
        while (i < raw.Length && IsDigit(raw[i]))
            i++;
        int intLength = i - intStart;

        int fracStart = 0, fracLength = 0;
        if (i < raw.Length && raw[i] == (byte)'.')
        {
            i++;
            fracStart = i;
            while (i < raw.Length && IsDigit(raw[i]))
                i++;
            fracLength = i - fracStart;
        }

        long exponent = 0;
        if (i < raw.Length && (raw[i] | 0x20) == 'e')
        {
            i++;
            bool exponentNegative = false;
            if (raw[i] == (byte)'+')
                i++;
            else if (raw[i] == (byte)'-')
            {
                exponentNegative = true;
                i++;
            }

            while (i < raw.Length && IsDigit(raw[i]))
            {
                // Saturate rather than overflow: exponents this large are already outside
                // anything another spelling could plausibly equal.
                if (exponent < 1_000_000_000_000L)
                    exponent = exponent * 10 + (raw[i] - (byte)'0');
                i++;
            }

            if (exponentNegative)
                exponent = -exponent;
        }

        // The significand is intDigits ++ fracDigits; value = significand * 10^(exponent - fracLength).
        int totalDigits = intLength + fracLength;
        byte DigitAt(ReadOnlySpan<byte> r, int k) => k < intLength ? r[intStart + k] : r[fracStart + (k - intLength)];

        int lead = 0;
        while (lead < totalDigits && DigitAt(raw, lead) == (byte)'0')
            lead++;

        if (lead == totalDigits)
        {
            // All zeros - including -0, 0.000, 0e5.
            dest[0] = (byte)'0';
            return 1;
        }

        int trail = 0;
        while (DigitAt(raw, totalDigits - 1 - trail) == (byte)'0')
            trail++;

        int digitCount = totalDigits - lead - trail;
        long e = exponent - fracLength + trail; // value = digits * 10^e

        int pos = 0;

        if (e >= 0 && digitCount + e + (negative ? 1 : 0) <= dest.Length)
        {
            // Plain integer spelling - identical to how the fast path sees a literal integer.
            if (negative)
                dest[pos++] = (byte)'-';
            for (int k = 0; k < digitCount; k++)
                dest[pos++] = DigitAt(raw, lead + k);
            for (long z = 0; z < e; z++)
                dest[pos++] = (byte)'0';
            return pos;
        }

        // Scientific: d[.ddd]e<E>, E = e + digitCount - 1.
        long scientificExponent = e + digitCount - 1;
        if (digitCount + 24 > dest.Length)
            return -1;

        if (negative)
            dest[pos++] = (byte)'-';
        dest[pos++] = DigitAt(raw, lead);
        if (digitCount > 1)
        {
            dest[pos++] = (byte)'.';
            for (int k = 1; k < digitCount; k++)
                dest[pos++] = DigitAt(raw, lead + k);
        }

        dest[pos++] = (byte)'e';
        if (scientificExponent < 0)
        {
            dest[pos++] = (byte)'-';
            scientificExponent = -scientificExponent;
        }

        // Manual itoa (most-significant first) - System.Buffers.Text.Utf8Formatter would
        // work too, but this keeps the digit order explicit and allocation-free.
        int expStart = pos;
        do
        {
            dest[pos++] = (byte)('0' + (int)(scientificExponent % 10));
            scientificExponent /= 10;
        } while (scientificExponent > 0);
        dest[expStart..pos].Reverse();

        return pos;
    }

    private static bool IsDigit(byte b) => (uint)(b - (byte)'0') <= 9;

    // ── Container accumulation ─────────────────────────────────────────────────────────
    //
    // Objects: State is a commutative (wrapping) sum of MixPair(nameHash, valueHash) per
    // member - O(1) space per open container, order-independence for free. Note where the
    // name goes: it is folded into the PARENT's accumulator, so a child's own hash never
    // contains its key (the relocation invariant in the class remarks).
    //
    // Arrays: State chains MixOrdered(State, valueHash) - order is semantic there.

    public static ulong MixPair(ulong nameHash, ulong valueHash)
        => Avalanche(nameHash * 0x9E3779B97F4A7C15UL ^ Avalanche(valueHash * 0xC2B2AE3D27D4EB4FUL));

    public static ulong MixOrdered(ulong state, ulong valueHash)
        => Avalanche(state * 0xA0761D6478BD642FUL ^ valueHash);

    public static ulong FinalizeObject(ulong state) => Avalanche(state ^ ObjectSalt);

    public static ulong FinalizeArray(ulong state) => Avalanche(state ^ ArraySalt);

    /// <summary>murmur3's 64-bit finalizer - full avalanche, so the weak combining above
    /// (sums, xors) never reaches another combining step un-mixed.</summary>
    private static ulong Avalanche(ulong h)
    {
        h ^= h >> 33;
        h *= 0xFF51AFD7ED558CCDUL;
        h ^= h >> 33;
        h *= 0xC4CEB9FE1A85EC53UL;
        h ^= h >> 33;
        return h;
    }
}
