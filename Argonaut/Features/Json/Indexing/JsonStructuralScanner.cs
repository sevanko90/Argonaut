using System;
using System.Buffers;
using System.Numerics;
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

    /// <summary>The scanner cannot answer: a comment before the value, a stray closing bracket,
    /// or the value runs past the end of a settled source. A full parse
    /// (<c>Utf8JsonReader.Skip</c>) gives the definitive answer, including the error.</summary>
    NeedsFullParse,
}

/// <summary>
/// Finds where a JSON value ends without tokenising it, at the speed of a memory scan: blocks are
/// classified by <see cref="JsonBlockClassifier"/>, and a subtree is skipped by counting brackets
/// over the masks. A block is examined bit by bit only when the value could end inside it.
/// Comments inside the value are read as whitespace.
///
/// It does not validate. It trusts that the bytes are JSON and answers
/// <see cref="JsonSkipOutcome.NeedsFullParse"/> for the cases it cannot decide cheaply, so a
/// caller keeps <c>Utf8JsonReader</c> as the fallback and as the source of error messages.
///
/// Must be started at a value (or whitespace before one), never inside a string or token: string
/// state is carried forward from the start, and a checkpoint is a value start by construction.
/// </summary>
public static class JsonStructuralScanner
{
    private const int BlockSize = JsonBlockClassifier.BlockSize;

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
            (byte)'/' or (byte)'}' or (byte)']' or (byte)',' or (byte)':' => JsonSkipOutcome.NeedsFullParse,
            _ => SkipBareScalar(source, valueStart, out end),
        };
    }

    private enum ValueShape
    {
        Container,
        String,
    }

    private static JsonSkipOutcome ScanBlocks(IByteSource source, long valueStart, ValueShape shape, out long end)
    {
        end = valueStart;
        var classifier = default(JsonBlockClassifier);
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
                classifier.Classify(span.Slice(i, BlockSize), out var masks);
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
            classifier.Classify(gathered, out var tail);
            if (Settle(tail, shape, blockOffset, ref depth, ref quotesSeen, out end) is { } finished)
                return finished;

            if (copied < BlockSize)
                return source.LengthSettled ? JsonSkipOutcome.NeedsFullParse : JsonSkipOutcome.Incomplete;

            blockOffset += BlockSize;
        }
    }

    /// <summary>Applies one block to the running count; returns an outcome once the value's end
    /// or a reason to stop has been found, else null to carry on.</summary>
    private static JsonSkipOutcome? Settle(in JsonBlockMasks masks, ValueShape shape, long blockOffset,
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

        int closes = BitOperations.PopCount(masks.Close);
        if (depth > closes)
        {
            // Cannot reach zero inside this block, so only the net change matters.
            depth += BitOperations.PopCount(masks.Open) - closes;
            return null;
        }

        ulong brackets = masks.Open | masks.Close;
        while (brackets != 0)
        {
            int bit = BitOperations.TrailingZeroCount(brackets);
            brackets &= brackets - 1;
            if ((masks.Open & (1UL << bit)) != 0)
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

    private static readonly SearchValues<byte> BareScalarDelimiters = SearchValues.Create(" \t\r\n,:]}/"u8);
}
