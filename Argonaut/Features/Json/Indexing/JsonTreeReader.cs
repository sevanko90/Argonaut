using System;
using System.Buffers;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Trees;

namespace Argonaut.Features.Json.Indexing;

/// <summary>
/// JSON for <see cref="TreeCursor"/>: finds the next member or element in a container and gets
/// past values without reading them. A member's row starts at its name; its value start - the
/// row's identity - is the value's first byte. Kinds are <see cref="JsonTokenKind"/>.
///
/// It reads what the tree reads - comments are skipped, a trailing comma separates nothing - and
/// does not validate: <see cref="JsonDocumentValidator"/> does that once for the whole document,
/// so reading here stays a scan for the next significant byte.
/// </summary>
public sealed class JsonTreeReader(IByteSource source) : ITreeFormatReader
{
    /// <summary>The document level's kind: outside <see cref="JsonTokenKind"/>'s range.</summary>
    public const byte Document = byte.MaxValue;

    private const int ReadStride = 4096;

    private static readonly SearchValues<byte> Whitespace = SearchValues.Create(" \t\r\n"u8);
    private static readonly SearchValues<byte> StringStops = SearchValues.Create("\"\\"u8);
    private static readonly SearchValues<byte> BareValueEnds = SearchValues.Create(" \t\r\n,:]}/"u8);

    public byte DocumentKind => Document;

    public bool TryReadChild(byte containerKind, ref long position, out TreeNode child, out long closeStart)
    {
        child = default;
        long at = SkipTrivia(position);
        if (Peek(at) == (byte)',')
            at = SkipTrivia(at + 1);

        closeStart = at;
        int next = Peek(at);
        if (next is < 0 or (byte)'}' or (byte)']')
            return false;

        long rowStart = at;
        if (containerKind == (byte)JsonTokenKind.StartObject && next == (byte)'"')
        {
            at = SkipTrivia(StringEnd(at));
            if (Peek(at) == (byte)':')
                at = SkipTrivia(at + 1);
            next = Peek(at);
            if (next < 0)
                return false;
        }

        position = at;
        child = next switch
        {
            (byte)'{' => new TreeNode(rowStart, at, -1, IsContainer: true, (byte)JsonTokenKind.StartObject),
            (byte)'[' => new TreeNode(rowStart, at, -1, IsContainer: true, (byte)JsonTokenKind.StartArray),
            (byte)'"' => new TreeNode(rowStart, at, StringEnd(at), IsContainer: false, (byte)JsonTokenKind.String),
            (byte)'t' => new TreeNode(rowStart, at, BareValueEnd(at), IsContainer: false, (byte)JsonTokenKind.True),
            (byte)'f' => new TreeNode(rowStart, at, BareValueEnd(at), IsContainer: false, (byte)JsonTokenKind.False),
            (byte)'n' => new TreeNode(rowStart, at, BareValueEnd(at), IsContainer: false, (byte)JsonTokenKind.Null),
            _ => new TreeNode(rowStart, at, BareValueEnd(at), IsContainer: false, (byte)JsonTokenKind.Number),
        };
        return true;
    }

    public long FirstChildPosition(long containerStart) => containerStart + 1;

    public long SkipValue(long containerStart) => JsonStructuralScanner.TrySkipValue(source, containerStart, out long end) switch
    {
        JsonSkipOutcome.Skipped => end,

        // Still arriving: the container runs on past everything read so far.
        JsonSkipOutcome.Incomplete => long.MaxValue,

        // Not JSON the scanner can follow. Validation reports the error; the tree treats the
        // rest of the document as this container's.
        _ => source.AvailableLength,
    };

    public long CloseStart(long containerStart, long containerEnd) => containerEnd - 1;

    /// <summary>One past the closing quote of the string whose opening quote is at
    /// <paramref name="quote"/>, or the end of the data for an unterminated one.</summary>
    public long StringEnd(long quote)
    {
        long at = quote + 1;
        while (true)
        {
            var span = source.GetContiguousSpan(at, ReadStride);
            if (span.IsEmpty)
                return at;

            int stop = span.IndexOfAny(StringStops);
            if (stop < 0)
            {
                at += span.Length;
                continue;
            }

            if (span[stop] == (byte)'"')
                return at + stop + 1;

            at += stop + 2; // a backslash and the byte it escapes
        }
    }

    private long BareValueEnd(long start)
    {
        long at = start;
        while (true)
        {
            var span = source.GetContiguousSpan(at, ReadStride);
            if (span.IsEmpty)
                return at;

            int stop = span.IndexOfAny(BareValueEnds);
            if (stop >= 0)
                return at + stop;

            at += span.Length;
        }
    }

    /// <summary>The first byte at or after <paramref name="at"/> that is not whitespace or part
    /// of a comment.</summary>
    private long SkipTrivia(long at)
    {
        while (true)
        {
            var span = source.GetContiguousSpan(at, ReadStride);
            if (span.IsEmpty)
                return at;

            int content = span.IndexOfAnyExcept(Whitespace);
            if (content < 0)
            {
                at += span.Length;
                continue;
            }

            at += content;
            if (span[content] != (byte)'/')
                return at;

            switch (Peek(at + 1))
            {
                case (byte)'/':
                    at = LineEnd(at + 2);
                    break;
                case (byte)'*':
                    at = BlockCommentEnd(at + 2);
                    break;
                default:
                    return at; // a lone slash: not JSON, and not trivia
            }
        }
    }

    private long LineEnd(long at)
    {
        while (true)
        {
            var span = source.GetContiguousSpan(at, ReadStride);
            if (span.IsEmpty)
                return at;

            int newline = span.IndexOf((byte)'\n');
            if (newline >= 0)
                return at + newline + 1;

            at += span.Length;
        }
    }

    private long BlockCommentEnd(long at)
    {
        // Comments are rare and short; a byte at a time keeps "*/" split across buffers simple.
        for (int previous = -1, current; (current = Peek(at)) >= 0; previous = current, at++)
        {
            if (previous == (byte)'*' && current == (byte)'/')
                return at + 1;
        }

        return at;
    }

    private int Peek(long at) => at < source.AvailableLength ? source.ByteAt(at) : -1;
}
