using System;
using System.Collections.Generic;
using System.Threading;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Progress;
using Argonaut.Engine.Text;

namespace Argonaut.Engine.Selection;

/// <summary>
/// Finds where each part of a <see cref="SelectionFilter"/> is in its file - what turns a filter
/// file into the runs a selection view shows.
///
/// <b>Lines</b> are found in one forward pass, the way <c>LineRangeLocator</c> finds one range: a
/// chunk is counted (<c>Count</c> is vectorised) and only walked newline by newline where a range
/// starts or ends in it. The pass stops at the end of the last range, so it reads everything
/// before that once; it belongs on a background thread, and blocks in
/// <see cref="IByteSource.WaitForLength"/> over a source still arriving. A range running past
/// the end of the file is cut short there; one starting past it is dropped and counted.
///
/// <b>Bytes</b> need no scan. Each range is taken as given, its start snapped back to the start of
/// the UTF-8 character it is in, exactly as <c>--bytes</c> takes one. Ranges are clipped to what
/// has arrived of the file.
/// </summary>
public static class LineSelectionLocator
{
    private const int ScanChunkSize = 4 * 1024 * 1024;

    /// <param name="checkpoints">Where earlier passes over the same file found its lines: a pass
    /// starts each range from the nearest one rather than reading up to it, and records the chunks
    /// it reads for the next. Null reads from the start every time.</param>
    public static LineSelection Locate(IByteSource source, SelectionFilter filter,
        IProgressReporter? progressReporter = null, CancellationToken cancellationToken = default, LineCheckpoints? checkpoints = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(filter);

        return filter.Unit == SelectionUnit.Lines
            ? LocateLines(source, filter.Ranges, checkpoints, progressReporter, cancellationToken)
            : LocateBytes(source, filter.Ranges);
    }

    private static LineSelection LocateLines(IByteSource source, ReadOnlySpan<SelectionRange> ranges,
        LineCheckpoints? checkpoints, IProgressReporter? progressReporter, CancellationToken cancellationToken)
    {
        var runs = new List<SelectedRun>();
        int r = 0;
        bool seekingStart = true;
        long runStart = 0;

        // Newlines counted before offset + pos. Line N starts after the (N-1)th newline and ends
        // after the Nth, so each range is two targets: First - 1 newlines, then Last.
        long newlines = 0;
        long offset = 0;
        var readAhead = new ReadAhead();

        try
        {
            // Chunked-scan loop deliberately duplicated (see FileOffsetIndex.ProduceAnchors).
            while (r < ranges.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Skip what an earlier pass already counted, up to the chunk the next target is in.
                if (checkpoints is not null)
                {
                    checkpoints.Record(offset, newlines);
                    var (known, knownNewlines) = checkpoints.Before(seekingStart ? ranges[r].First - 1 : ranges[r].Last);
                    if (known > offset)
                        (offset, newlines) = (known, knownNewlines);
                }

                long available = source.AvailableLength;
                if (offset >= available)
                {
                    if (source.LengthSettled)
                        break;

                    source.WaitForLength(offset + 1, cancellationToken);
                    continue;
                }

                readAhead.Reached(source, offset);
                var chunk = source.GetContiguousSpan(offset, (int)Math.Min(ScanChunkSize, available - offset));
                if (chunk.IsEmpty)
                    continue;

                int pos = 0;
                while (r < ranges.Length)
                {
                    var range = ranges[r];
                    long wanted = (seekingStart ? range.First - 1 : range.Last) - newlines;
                    if (wanted > 0)
                    {
                        if (pos == chunk.Length)
                            break;

                        int at = IndexOfNth(chunk[pos..], wanted, out int counted);
                        if (at < 0)
                        {
                            newlines += counted;
                            pos = chunk.Length;
                            break;
                        }

                        newlines += wanted;
                        pos += at + 1;
                    }

                    // A target reached: the start of the range's first line, or the end of its last.
                    if (seekingStart)
                    {
                        runStart = offset + pos;
                        seekingStart = false;
                    }
                    else
                    {
                        long end = offset + pos;
                        runs.Add(new SelectedRun(runStart, end - runStart, false, range.First, range.Last - range.First + 1));
                        r++;
                        seekingStart = true;
                    }
                }

                offset += chunk.Length;
                progressReporter?.Report("Finding lines", offset, available);
            }

            // The end of the file, with a range still open: it ends here, cut short. A range whose
            // first line would start here has no first line - nothing after a final newline is a line.
            if (r < ranges.Length && !seekingStart && source.AvailableLength > runStart)
            {
                long end = source.AvailableLength;
                bool newlineless = source.ByteAt(end - 1) != (byte)'\n';
                long first = ranges[r].First;
                runs.Add(new SelectedRun(runStart, end - runStart, newlineless, first, newlines - (first - 1) + (newlineless ? 1 : 0)));
                r++;
            }

            return new LineSelection(SelectionUnit.Lines, runs.ToArray(), ranges.Length - r);
        }
        finally
        {
            progressReporter?.Report("Finding lines", offset, offset);
        }
    }

    private static LineSelection LocateBytes(IByteSource source, ReadOnlySpan<SelectionRange> ranges)
    {
        var runs = new List<SelectedRun>();
        long length = source.AvailableLength;
        int pastEnd = 0;

        foreach (var range in ranges)
        {
            if (range.First >= length)
            {
                pastEnd++;
                continue;
            }

            long start = Utf8Boundary.CharacterStartAtOrBefore(source, range.First);
            long end = Math.Min(range.Last, length - 1) + 1;

            // The snap can only reach back into the run before when the two were within a
            // character of each other - then they are one run.
            if (runs.Count > 0 && runs[^1].FileOffset + runs[^1].Length >= start)
            {
                var previous = runs[^1];
                start = previous.FileOffset;
                runs.RemoveAt(runs.Count - 1);
            }

            runs.Add(new SelectedRun(start, end - start, source.ByteAt(end - 1) != (byte)'\n', 0, 0));
        }

        return new LineSelection(SelectionUnit.Bytes, runs.ToArray(), pastEnd);
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
