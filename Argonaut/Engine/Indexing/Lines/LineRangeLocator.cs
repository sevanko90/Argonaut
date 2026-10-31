using System;
using System.Threading;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Progress;

namespace Argonaut.Engine.Indexing.Lines;

/// <summary>
/// Lines <see cref="FirstLine"/>..<see cref="LastLine"/> of a document (1-based, inclusive) and
/// the bytes they occupy, from the start of the first through the newline ending the last - or
/// the end of the document, when the last line has none. <see cref="LastLine"/> is null for a
/// range that runs to the end of the document: counting that far would cost a scan of the whole
/// tail, which is exactly what an excerpt is opened to avoid.
/// </summary>
public readonly record struct LocatedLines(ByteRange Range, long FirstLine, long? LastLine);

/// <summary>
/// Finds where a run of lines sits in a document, before anything is open over it - what turns
/// <c>--lines 1000000-2000000</c> into the byte range an excerpt maps. A line is what the line
/// index counts: everything up to and including a <c>\n</c>.
///
/// One forward pass that never reads past the end of the last line asked for. A chunk the target
/// is not in is only counted (<c>Count</c> is vectorised); only the chunk holding it is walked
/// newline by newline. Nothing is allocated. It reads every byte before the range once, so it
/// belongs on a background thread - it blocks in <see cref="IByteSource.WaitForLength"/> over a
/// source that is still arriving.
/// </summary>
public static class LineRangeLocator
{
    private const int ScanChunkSize = 4 * 1024 * 1024;

    /// <summary>
    /// Locates lines <paramref name="firstLine"/> to <paramref name="lastLine"/> (to the end of
    /// the document when null). A range running past the end is cut short there - the result's
    /// <see cref="LocatedLines.LastLine"/> says where it really ended. Null when the document has
    /// no line <paramref name="firstLine"/>.
    /// </summary>
    public static LocatedLines? Locate(IByteSource source, long firstLine, long? lastLine,
        IProgressReporter? progressReporter = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(firstLine, 1);
        if (lastLine is { } last)
            ArgumentOutOfRangeException.ThrowIfLessThan(last, firstLine);

        // Line N starts after the (N-1)th newline and ends after the Nth.
        long newlinesBeforeStart = firstLine - 1;
        long newlines = 0;
        long offset = 0;
        long start = firstLine == 1 ? 0 : -1;
        long end = -1;
        var readAhead = new ReadAhead();

        try
        {
            // Chunked-scan loop deliberately duplicated (see FileOffsetIndex.ProduceAnchors).
            while (end < 0 && (start < 0 || lastLine is not null))
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool settled = source.LengthSettled; // before the length: see FileOffsetIndex.ProduceAnchors
                long available = source.AvailableLength;
                if (offset >= available)
                {
                    if (settled)
                        break;

                    source.WaitForLength(offset + 1, cancellationToken);
                    continue;
                }

                readAhead.Reached(source, offset);
                var chunk = source.GetContiguousSpan(offset, (int)Math.Min(ScanChunkSize, available - offset));
                if (chunk.IsEmpty)
                    continue;

                int pos = 0;
                while (pos < chunk.Length)
                {
                    long wanted = (start < 0 ? newlinesBeforeStart : lastLine!.Value) - newlines;
                    var rest = chunk.Slice(pos);
                    int at = IndexOfNth(rest, wanted, out int counted);
                    if (at < 0)
                    {
                        newlines += counted;
                        break;
                    }

                    newlines += wanted;
                    pos += at + 1;
                    if (start < 0)
                    {
                        start = offset + pos;
                        if (lastLine is null)
                            break;
                    }
                    else
                    {
                        end = offset + pos;
                        break;
                    }
                }

                offset += chunk.Length;
                progressReporter?.Report("Finding lines", offset, available);
            }

            if (start < 0)
                return null;

            if (end < 0)
            {
                // To the end of the document: what has arrived is only an end once it is final.
                while (!source.LengthSettled)
                    source.WaitForLength(source.AvailableLength + 1, cancellationToken);

                end = source.AvailableLength;

                // Nothing after the start - an empty document, or an empty tail after a final
                // newline - is not a line.
                if (end == start)
                    return null;
            }

            return new LocatedLines(new ByteRange(start, end - start), firstLine, LastLineReached(source, end, newlines, lastLine));
        }
        finally
        {
            progressReporter?.Report("Finding lines", offset, offset);
        }
    }

    /// <summary>The last line the range really holds: the one asked for when its newline was
    /// found, otherwise the line the end of the document cut it short in - or null when it was
    /// open-ended and nothing past its start was counted.</summary>
    private static long? LastLineReached(IByteSource source, long end, long newlines, long? lastLine)
    {
        if (lastLine is not { } last)
            return null;

        if (newlines == last)
            return last;

        return source.ByteAt(end - 1) == (byte)'\n' ? newlines : newlines + 1;
    }

    /// <summary>The index of the <paramref name="n"/>th newline in <paramref name="span"/>, or -1
    /// with every newline it holds counted into <paramref name="counted"/> when it has fewer.</summary>
    private static int IndexOfNth(ReadOnlySpan<byte> span, long n, out int counted)
    {
        counted = span.Count((byte)'\n');
        if (counted < n)
            return -1;

        int pos = -1;
        for (long i = 0; i < n; i++)
            pos += span.Slice(pos + 1).IndexOf((byte)'\n') + 1;

        return pos;
    }
}
