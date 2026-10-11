using System;
using System.Buffers;
using System.Collections.Generic;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Hints;
using Argonaut.Engine.Text;
using Argonaut.Features.Raw.Highlighting;
using Argonaut.Features.Raw.Rows;
using Argonaut.Ui.Hints;

namespace Argonaut.Features.Raw.Hints;

/// <summary>One row of the raw view as hints see it: its drawn text, where it sits in the
/// document's bytes, and whether it starts its line or wraps onto the next row.</summary>
public readonly record struct RawHintRow(RawDecodedRow Decoded, bool IsLineStart, bool IsSoftWrapped)
{
    public string Text => Decoded.Text;

    public long Start => Decoded.RowStart;
}

/// <summary>
/// A value carried from one row to the next row of the same line: a string or number the row
/// ended inside of, so the next row knows the text it starts with belongs to a value that began
/// above. <see cref="Unknown"/> when the row's own entry could not be told.
/// </summary>
public readonly record struct RawHintCarry(long ValueStart, bool FirstOnLine)
{
    public static RawHintCarry None => new(-1, false);

    public static RawHintCarry Unknown => new(-2, false);

    public bool IsOpen => ValueStart >= 0;
}

/// <summary>A value as read from the document: where it starts (at its opening quote, if any),
/// where its content runs, where it ends, what it reads as - <see cref="Hint"/> null when
/// nothing - and whether only punctuation follows it to the end of its line.</summary>
public sealed record RawValue(long ValueStart, long ContentStart, long ContentEnd, long ValueEnd, ValueHint? Hint, bool EndsLine = false);

/// <summary>A hinted value's share of one row, in the row's characters. <see cref="IsChip"/>
/// when the value's hint is a chip after its line's text rather than a mark on the value - drawn
/// on the row it <see cref="EndsHere"/>, and nothing on the rows before.</summary>
public readonly record struct RawHintMark(int Start, int End, RawValue Value, bool IsChip, bool EndsHere)
{
    public ValueHint Hint => Value.Hint!;
}

/// <summary>
/// The value hints of the raw view's rows. The lexer finds the values - its string and number
/// spans, keys excluded - and this reads each one whole from the document's bytes, so a value
/// wrapped over several rows is classified once, as itself. Each value's extent and hint is kept
/// by where it starts, so scrolling re-reads nothing; an edit shifts every offset after it, so
/// the owner calls <see cref="Forget"/> when the bytes change.
///
/// A hint is a chip at the end of the line when its value is the line's only one and nothing but
/// punctuation follows it - a pretty-printed member, a config pair - the way the tree shows it;
/// anywhere else (a minified line, several values) it marks the value itself.
/// </summary>
public sealed class RawValueHints
{
    /// <summary>The most bytes followed looking for a value's end. Past it a value is not hinted:
    /// nothing reads that much on screen, and a hint card would not decode it either.</summary>
    public const int MaxValueBytes = HintExpansion.MaxValueBytes;

    /// <summary>Values kept resolved; more than a few screens' worth, and the cache starts over.</summary>
    private const int CacheCapacity = 4096;

    private readonly IReadOnlyList<IValueHintProvider> providers;
    private readonly DateHintSettings dates;
    private readonly Dictionary<long, RawValue?> resolved = new();
    private IByteSource? source;

    public RawValueHints(IReadOnlyList<IValueHintProvider> providers, DateHintSettings dates)
    {
        this.providers = providers;
        this.dates = dates;
        foreach (var provider in providers)
            provider.HintsChanged += OnProviderHintsChanged;
    }

    /// <summary>What reads the values, in order - the first to read one wins.</summary>
    public IReadOnlyList<IValueHintProvider> Providers => providers;

    /// <summary>The bytes the rows were decoded from. Setting it forgets every value read so far.</summary>
    public IByteSource? Source
    {
        get => source;
        set
        {
            source = value;
            resolved.Clear();
        }
    }

    /// <summary>Raised (UI thread) when hints already shown may now read differently.</summary>
    public event EventHandler? HintsChanged;

    /// <summary>Forgets every value read so far - the bytes changed under them.</summary>
    public void Forget() => resolved.Clear();

    /// <summary>
    /// Appends the hint marks of one row to <paramref name="marks"/> (which the caller clears)
    /// and returns what the row hands its successor. <paramref name="spans"/> are the row's lexer
    /// spans and <paramref name="entry"/> what its predecessor handed on - <see cref="RawHintCarry.None"/>
    /// at a line start. With <paramref name="marks"/> null only the carry is worked out, for a
    /// row lexed to reach a later one.
    /// </summary>
    public RawHintCarry MarkRow(in RawHintRow row, RawHintCarry entry, List<RawStyledSpan> spans, List<RawHintMark>? marks)
    {
        var carry = RawHintCarry.None;
        bool seenValue = false;
        long rowEnd = row.Start + row.Decoded.DisplayByteLength;
        long coveredUntil = row.Start;
        for (int i = 0; i < spans.Count; i++)
        {
            var span = spans[i];
            if (span.Style is not (RawTextStyle.String or RawTextStyle.Number))
                continue;

            // A bare config value is the rest of its line, which the lexer may colour as several
            // words: the first one stands for the value, and the rest are inside it.
            if (row.Start + row.Decoded.ByteOffsetForChar(span.Start) < coveredUntil)
                continue;

            bool reachesEnd = span.Start + span.Length >= row.Text.Length && row.IsSoftWrapped;
            bool continued = span.Start == 0 && !row.IsLineStart && entry != RawHintCarry.None;
            if (continued && !entry.IsOpen)
            {
                // A row that began inside a value it cannot trace back is left plain, and so is the
                // rest of that value.
                if (reachesEnd)
                    carry = RawHintCarry.Unknown;
                seenValue = true;
                continue;
            }

            long valueStart = continued ? entry.ValueStart : row.Start + row.Decoded.ByteOffsetForChar(span.Start);
            bool firstOnLine = continued ? entry.FirstOnLine : row.IsLineStart && !seenValue;
            seenValue = true;

            var value = Resolve(valueStart, span.Style == RawTextStyle.Number ? ValueShape.Number : ValueShape.String);
            if (value is not null)
                coveredUntil = Math.Max(coveredUntil, value.ValueEnd);
            if (reachesEnd)
                carry = value is null ? RawHintCarry.Unknown : value.ValueEnd > rowEnd ? new RawHintCarry(valueStart, firstOnLine) : RawHintCarry.None;

            if (marks is not null && value?.Hint is not null)
                AddMark(row, value, firstOnLine, marks);
        }

        return carry;
    }

    /// <summary>The mark for a value's share of a row: its content's characters on this row, and
    /// whether it is its line's one value, with only punctuation after it.</summary>
    private static void AddMark(in RawHintRow row, RawValue value, bool firstOnLine, List<RawHintMark> marks)
    {
        long rowEnd = row.Start + row.Decoded.DisplayByteLength;
        long from = Math.Max(value.ContentStart, row.Start);
        long to = Math.Min(value.ContentEnd, rowEnd);
        if (to < from || (to == from && value.ContentEnd > value.ContentStart))
            return;

        int start = row.Decoded.CharIndexForByte((int)(from - row.Start));
        int end = to >= rowEnd ? row.Text.Length : row.Decoded.CharIndexForByte((int)(to - row.Start));

        bool endsHere = value.ValueEnd <= rowEnd;
        marks.Add(new RawHintMark(start, end, value, firstOnLine && value.EndsLine, endsHere));
    }

    /// <summary>The value starting at <paramref name="valueStart"/>, read once and kept; null
    /// when it does not end where a value must.</summary>
    public RawValue? Resolve(long valueStart, ValueShape shape)
    {
        if (resolved.TryGetValue(valueStart, out var known))
            return known;

        if (resolved.Count >= CacheCapacity)
            resolved.Clear();

        var value = source is null ? null : Read(source, valueStart, shape);
        resolved[valueStart] = value;
        return value;
    }

    private RawValue? Read(IByteSource bytes, long valueStart, ValueShape shape)
    {
        if (!FindExtent(bytes, valueStart, ref shape, out long contentStart, out long contentEnd, out long valueEnd))
            return null;

        long length = contentEnd - contentStart;
        if (length <= 0)
            return new RawValue(valueStart, contentStart, contentEnd, valueEnd, null);

        int shown = (int)Math.Min(length, DisplayText.MaxLength);
        byte[] rented = ArrayPool<byte>.Shared.Rent(shown);
        try
        {
            var raw = rented.AsSpan(0, shown);
            bytes.CopyTo(contentStart, raw);
            if (shape == ValueShape.Number)
                InferDateScheme(raw);

            var hint = ValueHints.Read(providers, shape, raw, length, valueStart);
            return new RawValue(valueStart, contentStart, contentEnd, valueEnd, hint, hint is not null && OnlyPunctuationToLineEnd(bytes, valueEnd));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Whether nothing but blanks, closing punctuation and a comment follow a value on its line -
    /// what lets its hint sit at the line's end as the tree's would. A long tail is not looked through.</summary>
    private static bool OnlyPunctuationToLineEnd(IByteSource bytes, long from)
    {
        long limit = Math.Min(bytes.AvailableLength, from + 256);
        for (long at = from; at < limit; at++)
        {
            byte b = bytes.ByteAt(at);
            if (b == (byte)'\n')
                return true;

            // A comment closing the line is still the line's end, as far as the value is concerned.
            if (b is (byte)'#' or (byte)';' or (byte)'/' && at > from && bytes.ByteAt(at - 1) is (byte)' ' or (byte)'\t')
                return true;
            if (b is not ((byte)' ' or (byte)'\t' or (byte)',' or (byte)';' or (byte)']' or (byte)'}' or (byte)'\r'))
                return false;
        }

        return limit == bytes.AvailableLength;
    }

    /// <summary>The first number on screen that reads as a date sets the document's scheme, the
    /// way the tree's first thousands of values do - unless the user already chose one.</summary>
    private void InferDateScheme(ReadOnlySpan<byte> raw)
    {
        if (dates.IsUserSelected || dates.FileDefaultScheme != DateDecodingScheme.Off)
            return;

        if (DateHintClassifier.TryClassify(raw, out _, out var scheme))
            dates.TrySetInferredDefault(scheme);
    }

    /// <summary>
    /// Where a value's content runs, read from its first byte: a double-quoted string to its
    /// closing unescaped quote, a single-quoted one to its next quote, a number while it reads as
    /// one, and a bare config value to the line's end, less a trailing comment and blanks. False
    /// when it does not end on its line within <see cref="MaxValueBytes"/>. A number that turns out
    /// to begin a bare value comes back as a <see cref="ValueShape.String"/>.
    /// </summary>
    internal static bool FindExtent(IByteSource bytes, long valueStart, ref ValueShape shape,
        out long contentStart, out long contentEnd, out long valueEnd)
    {
        contentStart = contentEnd = valueEnd = valueStart;
        if (valueStart >= bytes.AvailableLength)
            return false;

        byte first = bytes.ByteAt(valueStart);
        if (first is (byte)'"' or (byte)'\'')
        {
            contentStart = valueStart + 1;
            if (!FindClosingQuote(bytes, contentStart, first, out contentEnd))
                return false;
            valueEnd = contentEnd + 1;
            return true;
        }

        if (shape == ValueShape.Number)
        {
            long at = valueStart;
            while (at < bytes.AvailableLength && at - valueStart < 64 && IsNumberByte(bytes.ByteAt(at)))
                at++;
            if (EndsNumber(bytes, at))
            {
                contentEnd = valueEnd = at;
                return true;
            }

            // Not a number after all but the start of a bare value - "0 2 * * *" - which runs
            // to the line's end.
            shape = ValueShape.String;
        }

        if (!FindLineEnd(bytes, valueStart, out long lineEnd))
            return false;
        contentEnd = valueEnd = TrimBareValue(bytes, valueStart, lineEnd);
        return true;
    }

    /// <summary>Whether a number ends at <paramref name="at"/>: what follows it, past blanks, is
    /// the line's end, a separator, a closing bracket or a comment.</summary>
    private static bool EndsNumber(IByteSource bytes, long at)
    {
        long limit = Math.Min(bytes.AvailableLength, at + 64);
        while (at < limit && bytes.ByteAt(at) is (byte)' ' or (byte)'\t')
            at++;
        return at >= bytes.AvailableLength
            || bytes.ByteAt(at) is (byte)'\n' or (byte)'\r' or (byte)',' or (byte)']' or (byte)'}' or (byte)'#' or (byte)';' or (byte)'/';
    }

    private static bool IsNumberByte(byte b) => b is >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'+' or (byte)'.' or (byte)'e' or (byte)'E';

    private static bool FindClosingQuote(IByteSource bytes, long from, byte quote, out long close)
    {
        close = -1;
        bool escaped = false;
        long limit = Math.Min(bytes.AvailableLength, from + MaxValueBytes);
        for (long at = from; at < limit;)
        {
            var chunk = bytes.GetContiguousSpan(at, (int)Math.Min(limit - at, 64 * 1024));
            if (chunk.IsEmpty)
                return false;

            for (int i = 0; i < chunk.Length; i++)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                // Only a double-quoted string escapes; YAML doubles a single quote instead, which
                // reads here as one string ending and another beginning - close enough for a hint.
                int next = quote == (byte)'"' ? chunk[i..].IndexOfAny((byte)'"', (byte)'\\', (byte)'\n') : chunk[i..].IndexOfAny(quote, (byte)'\n');
                if (next < 0)
                    break;

                i += next;
                byte b = chunk[i];
                if (b == (byte)'\n')
                    return false;
                if (b == (byte)'\\')
                {
                    escaped = true;
                    continue;
                }

                close = at + i;
                return true;
            }

            at += chunk.Length;
        }

        return false;
    }

    private static bool FindLineEnd(IByteSource bytes, long from, out long lineEnd)
    {
        long limit = Math.Min(bytes.AvailableLength, from + MaxValueBytes);
        for (long at = from; at < limit;)
        {
            var chunk = bytes.GetContiguousSpan(at, (int)Math.Min(limit - at, 64 * 1024));
            if (chunk.IsEmpty)
                break;

            int newline = chunk.IndexOfAny((byte)'\n', (byte)'\r');
            if (newline >= 0)
            {
                lineEnd = at + newline;
                return true;
            }

            at += chunk.Length;
        }

        lineEnd = limit;
        return limit == bytes.AvailableLength && limit - from < MaxValueBytes;
    }

    /// <summary>A bare value's end: before a comment that a blank introduces, then before
    /// trailing blanks - the config lexer's rule for where a value stops.</summary>
    private static long TrimBareValue(IByteSource bytes, long start, long lineEnd)
    {
        long end = lineEnd;
        for (long at = start + 1; at < lineEnd; at++)
        {
            byte b = bytes.ByteAt(at);
            if (b is (byte)'#' or (byte)';' && bytes.ByteAt(at - 1) is (byte)' ' or (byte)'\t')
            {
                end = at;
                break;
            }
        }

        while (end > start && bytes.ByteAt(end - 1) is (byte)' ' or (byte)'\t')
            end--;
        return end;
    }

    private void OnProviderHintsChanged(object? sender, EventArgs e)
    {
        resolved.Clear();
        HintsChanged?.Invoke(this, EventArgs.Empty);
    }
}
