using System;
using System.Buffers;
using System.Text;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Raw;

/// <summary>
/// One display row's text together with the byte each character came from - what
/// <see cref="RawRowReader"/> produces, plus the mapping it throws away.
///
/// The raw viewer's decode is lossy in four directions at once: a multi-byte UTF-8 sequence
/// collapses to one char (or a surrogate pair), an invalid byte run collapses to a single U+FFFD,
/// a C0 control is substituted by its Control Picture, and a trailing newline is dropped. So a
/// character's index says nothing about its byte offset, and a caret - which lives in byte
/// offsets, because that is what an edit and the row index both speak - cannot be positioned
/// without this map.
/// </summary>
public sealed class RawDecodedRow
{
    private readonly int[] byteOffsets;

    internal RawDecodedRow(string text, int[] byteOffsets, long rowStart, int displayByteLength)
    {
        Text = text;
        this.byteOffsets = byteOffsets;
        RowStart = rowStart;
        DisplayByteLength = displayByteLength;
    }

    /// <summary>The row as it is drawn.</summary>
    public string Text { get; }

    /// <summary>Absolute offset of the row's first byte.</summary>
    public long RowStart { get; }

    /// <summary>
    /// Bytes this row displays - the row's byte length minus any trailing newline, which is part
    /// of the row's range but is never drawn and never holds the caret.
    /// </summary>
    public int DisplayByteLength { get; }

    /// <summary>
    /// Offset, relative to <see cref="RowStart"/>, of the byte that produced character
    /// <paramref name="charIndex"/>. Defined at <c>Text.Length</c> too, where it is
    /// <see cref="DisplayByteLength"/> - the caret position at the end of the row.
    /// </summary>
    public int ByteOffsetForChar(int charIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(charIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(charIndex, Text.Length);
        return this.byteOffsets[charIndex];
    }

    /// <summary>
    /// The character containing the byte at <paramref name="byteOffset"/> (relative to
    /// <see cref="RowStart"/>). A byte in the middle of a multi-byte sequence, or inside an
    /// invalid run, reports the character that sequence produced - so a caret dropped anywhere
    /// inside a character lands consistently on it rather than between its bytes.
    /// </summary>
    public int CharIndexForByte(int byteOffset)
    {
        if (byteOffset <= 0)
            return 0;
        if (byteOffset >= DisplayByteLength)
            return Text.Length;

        // Greatest char whose byte offset is at or before the target.
        int lo = 0, hi = Text.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo + 1) / 2;
            if (this.byteOffsets[mid] <= byteOffset)
                lo = mid;
            else
                hi = mid - 1;
        }

        return lo;
    }

    /// <summary>
    /// True when <paramref name="byteOffset"/> (relative to <see cref="RowStart"/>) is the first
    /// byte of a character, or the row end - i.e. somewhere the caret may legally sit.
    /// </summary>
    public bool IsCharacterBoundary(int byteOffset)
    {
        if (byteOffset == DisplayByteLength)
            return true;
        if (byteOffset < 0 || byteOffset > DisplayByteLength)
            return false;

        return this.byteOffsets[CharIndexForByte(byteOffset)] == byteOffset;
    }
}

/// <summary>
/// Decodes a row the way <see cref="RawRowReader"/> does, but recording where every character
/// came from. Kept as a separate entry point rather than folded into the reader because the
/// read-only path has no use for the map and should not pay to build it.
/// </summary>
public static class RawRowDecoder
{
    public static RawDecodedRow Decode(IByteSource source, long start, long endExclusive, bool isSoftWrapped)
    {
        int length = (int)(endExclusive - start);
        if (length <= 0)
            return new RawDecodedRow(string.Empty, [0], start, 0);

        var contiguous = source.GetContiguousSpan(start, length);
        if (contiguous.Length == length)
            return DecodeSpan(contiguous, start, isSoftWrapped);

        byte[] gathered = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            int copied = source.CopyTo(start, gathered.AsSpan(0, length));
            return DecodeSpan(gathered.AsSpan(0, copied), start, isSoftWrapped);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(gathered);
        }
    }

    private static RawDecodedRow DecodeSpan(ReadOnlySpan<byte> row, long rowStart, bool isSoftWrapped)
    {
        // Same rule as RawRowReader: only a real line end can carry newline bytes.
        if (!isSoftWrapped)
        {
            while (row.Length > 0 && row[^1] is (byte)'\n' or (byte)'\r')
                row = row[..^1];
        }

        // Every byte yields at most one char, so the row's byte length is an upper bound on both
        // buffers - and the offsets array carries one extra entry for the end-of-row position.
        // Both are rented and copied out exactly once, so a decode costs two right-sized
        // allocations rather than a StringBuilder's growth chain.
        char[] chars = ArrayPool<char>.Shared.Rent(row.Length);
        int[] offsets = ArrayPool<int>.Shared.Rent(row.Length + 1);
        try
        {
            Span<char> encoded = stackalloc char[2];
            int consumed = 0;
            int charCount = 0;
            while (consumed < row.Length)
            {
                // DecodeFromUtf8 reports how much it consumed even when the bytes are invalid or
                // run out mid-sequence, applying the same maximal-subpart rule as UTF8's
                // replacement fallback - which is why the text it produces matches
                // RawRowReader's, and why that equivalence is worth asserting in a test.
                var status = Rune.DecodeFromUtf8(row[consumed..], out Rune rune, out int bytesConsumed);
                if (status != OperationStatus.Done)
                    rune = Rune.ReplacementChar; // already what it returns; stated, not relied on

                int charsWritten = rune.EncodeToUtf16(encoded);
                for (int i = 0; i < charsWritten; i++)
                {
                    chars[charCount] = RawRowReader.SubstituteControl(encoded[i]);
                    offsets[charCount] = consumed;
                    charCount++;
                }

                consumed += bytesConsumed;
            }

            offsets[charCount] = row.Length;

            return new RawDecodedRow(
                new string(chars.AsSpan(0, charCount)),
                offsets.AsSpan(0, charCount + 1).ToArray(),
                rowStart,
                row.Length);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
            ArrayPool<int>.Shared.Return(offsets);
        }
    }
}
