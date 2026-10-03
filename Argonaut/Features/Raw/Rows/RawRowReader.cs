using System;
using System.Buffers;
using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Text;

namespace Argonaut.Features.Raw.Rows;

/// <summary>
/// Decodes one indexed display row to text straight from the underlying bytes. The raw viewer
/// makes no assumptions about content, so decoding must never choke: invalid UTF-8 becomes
/// U+FFFD (the default decoder fallback), and control characters are drawn as glyphs
/// (<see cref="ControlGlyphs"/>) so a binary row renders visibly rather than upsetting text layout.
/// Every byte yields at most one char, so a row's display text is never longer than its
/// (cap-bounded) byte length - an upper bound, not a 1:1 mapping. A caller that needs to know
/// which byte a given char came from (the caret) wants <see cref="RawRowDecoder"/> instead.
/// </summary>
public static class RawRowReader
{
    public static string ReadRow(IByteSource source, long start, long endExclusive, bool isSoftWrapped)
    {
        int length = (int)(endExclusive - start);
        if (length <= 0)
            return string.Empty;

        // Fast path: a row served whole (always, over a plain mapping) decodes with no copy.
        var contiguous = source.GetContiguousSpan(start, length);
        if (contiguous.Length == length)
            return Decode(contiguous, isSoftWrapped);

        // The row straddles a piece boundary, so gather it first. Rows are cap-bounded, so this
        // rents kilobytes at most.
        byte[] gathered = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            int copied = source.CopyTo(start, gathered.AsSpan(0, length));
            return Decode(gathered.AsSpan(0, copied), isSoftWrapped);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(gathered);
        }
    }

    private static string Decode(ReadOnlySpan<byte> row, bool isSoftWrapped)
    {
        // Only a real line end can carry newline bytes (a soft-wrapped segment never ends in
        // '\n' - the indexer's peek rule pulls one at the cap into the segment as a real end).
        if (!isSoftWrapped)
        {
            while (row.Length > 0 && row[^1] is (byte)'\n' or (byte)'\r')
                row = row[..^1];
        }

        return ControlGlyphs.ForDisplay(Encoding.UTF8.GetString(row));
    }
}
