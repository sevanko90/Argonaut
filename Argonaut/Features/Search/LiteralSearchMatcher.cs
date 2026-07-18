using System;
using System.Text;

namespace Argonaut.Features.Search;

/// <summary>
/// Finds literal occurrences of a term in raw UTF-8 file bytes. Case-insensitivity is
/// ASCII-only: non-ASCII bytes must match exactly. Matching is against the on-disk bytes,
/// so an escaped form inside a JSON string (e.g. backslash-u0041 for "A") only matches if
/// the term is typed the same way - a decoded-text matcher is the future fix, not this class.
/// </summary>
public sealed class LiteralSearchMatcher : ISearchMatcher
{
    private readonly byte[] needle;
    private readonly bool ignoreCase;
    private readonly byte firstLower;
    private readonly byte firstUpper;

    public LiteralSearchMatcher(string term, bool ignoreCase = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(term);

        needle = Encoding.UTF8.GetBytes(term);
        this.ignoreCase = ignoreCase;
        firstLower = ToLowerAscii(needle[0]);
        firstUpper = ToUpperAscii(needle[0]);
    }

    public int WindowOverlap => needle.Length - 1;

    public bool TryFindNext(ReadOnlySpan<byte> window, int from, out int matchIndex, out int matchLength)
    {
        matchIndex = -1;
        matchLength = needle.Length;
        int lastStart = window.Length - needle.Length;

        while (from >= 0 && from <= lastStart)
        {
            // Vectorized candidate scan on the first byte (both cases when folding), then a
            // cheap ASCII-folded verify of the remainder - same "SIMD for the hot part"
            // shape as FileOffsetIndex's newline scan.
            var slice = window.Slice(from);
            int candidate = ignoreCase && firstLower != firstUpper
                ? slice.IndexOfAny(firstLower, firstUpper)
                : slice.IndexOf(needle[0]);
            if (candidate < 0)
                return false;

            int start = from + candidate;
            if (start > lastStart)
                return false;

            if (Matches(window.Slice(start, needle.Length)))
            {
                matchIndex = start;
                return true;
            }

            from = start + 1;
        }

        return false;
    }

    private bool Matches(ReadOnlySpan<byte> candidate)
    {
        if (!ignoreCase)
            return candidate.SequenceEqual(needle);

        for (int i = 0; i < needle.Length; i++)
        {
            if (ToLowerAscii(candidate[i]) != ToLowerAscii(needle[i]))
                return false;
        }

        return true;
    }

    private static byte ToLowerAscii(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;

    private static byte ToUpperAscii(byte b) => b is >= (byte)'a' and <= (byte)'z' ? (byte)(b - 32) : b;
}
