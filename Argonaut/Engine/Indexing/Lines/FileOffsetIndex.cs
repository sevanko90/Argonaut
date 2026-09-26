using System;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Collections;
using Argonaut.Engine.Progress;

namespace Argonaut.Engine.Indexing.Lines;

/// <summary>
/// One line's bytes, its newline included.
/// </summary>
/// <param name="Offset">Byte offset into the file</param>
/// <param name="Length">Number of bytes to index</param>
public readonly record struct FileLineSpan(long Offset, int Length);

/// <summary>Where a line starts, and its number from 0 - one of <see cref="FileOffsetIndex"/>'s
/// sparse records.</summary>
public readonly record struct FileLineAnchor(long Offset, int Line);

/// <summary>
/// A finished scan's anchors, detached from the index that built them so they can be kept past
/// that session (see <see cref="KeptIndexes"/>) and bound again to a source over the same bytes
/// with <see cref="FileOffsetIndex.Reopen"/>. Holds no source.
/// </summary>
public sealed class FileLineAnchors
{
    internal FileLineAnchors(SegmentedAppendLog<FileLineAnchor> log, int lineCount, long length)
    {
        Log = log;
        LineCount = lineCount;
        Length = length;
    }

    /// <summary>The key a document's line anchors are kept under.</summary>
    public static object Key { get; } = typeof(FileLineAnchors);

    internal SegmentedAppendLog<FileLineAnchor> Log { get; }

    public int LineCount { get; }

    /// <summary>The length of the bytes they were built over.</summary>
    public long Length { get; }
}

/// <summary>
/// The lines of a file (NDJSON, CSV), addressed by number or by byte offset. Sparse: it stores
/// where a line starts only once <see cref="AnchorBytes"/> bytes or <see cref="AnchorLines"/>
/// lines have passed since the last one, and finds the lines between by searching forward for
/// newlines - so a lookup reads at most about that much, and the index is 16 bytes per anchor
/// (a quarter of a MB per GB of typical rows) rather than 16 bytes per line. Bounding by bytes as
/// well as lines is what keeps a file of very long lines cheap to look into.
///
/// The line count is published separately from the anchors, never ahead of them, and the base's
/// waits follow it (see <see cref="AppendLogIndexBase{T}.PublishedCount"/>). The source is held
/// for the lookups, so it must outlive the index - the session that owns both sees to that.
/// </summary>
public sealed class FileOffsetIndex : AppendLogIndexBase<FileLineAnchor>, IBackgroundIndex
{
    /// <summary>Bytes after which the next line start gets an anchor.</summary>
    internal const int AnchorBytes = 64 * 1024;

    /// <summary>Lines after which the next line start gets an anchor - bounds the walk through
    /// a run of very short lines.</summary>
    internal const int AnchorLines = 1024;

    // Size of the chunk scanned per outer-loop pass. Scanning is zero-copy (spans over the
    // mapped file), so this only bounds progress-reporting granularity, how often the line count
    // is published, and span length - nothing is allocated per chunk.
    private const int ScanChunkSize = 4 * 1024 * 1024;

    // Bytes asked of the source per step of a lookup's newline search.
    private const int WalkChunkSize = 64 * 1024;

    private readonly IByteSource source;

    // Lines whose end is known, and one past the last byte of the last of them - published
    // together, once per scan chunk, so a reader never pairs one chunk's count with another's end.
    private Coverage published = Coverage.None;

    // The last line a lookup reached: a screen of consecutive rows walks on from the previous
    // row instead of from its anchor. Lookups come from the UI and from background readers alike.
    private readonly Lock walkSync = new();
    private int walkLine = -1;
    private long walkOffset;

    private FileOffsetIndex(IByteSource source)
    {
        this.source = source;
    }

    private FileOffsetIndex(IByteSource source, FileLineAnchors anchors)
        : base(anchors.Log)
    {
        this.source = source;
        this.published = new Coverage(anchors.LineCount, anchors.Length);
    }

    public Task IndexingTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Returns the number of lines in the index (may be less than the actual number of lines until <see cref="AppendLogIndexBase{T}.AllItemsPublished"/> is true).
    /// </summary>
    public int LineCount => Volatile.Read(ref this.published).Lines;

    /// <summary>One past the last byte of the last line counted - how far a byte offset can be
    /// resolved to a line so far.</summary>
    public long CoveredLength => Volatile.Read(ref this.published).End;

    protected override int PublishedCount => LineCount;

    /// <summary>
    /// An index over <paramref name="source"/> built from anchors a finished scan of the same
    /// bytes left behind: complete at once, no scan. The caller vouches that the bytes are the
    /// same (a <see cref="ByteOriginVersion"/> match); the length is checked here as well, because
    /// a line walk past the end of a shorter source would read out of bounds.
    /// </summary>
    public static FileOffsetIndex Reopen(IByteSource source, FileLineAnchors anchors)
    {
        if (source.AvailableLength != anchors.Length || !source.LengthSettled)
            throw new ArgumentException("The anchors were built over different bytes.", nameof(anchors));

        return new FileOffsetIndex(source, anchors);
    }

    /// <summary>
    /// This index's anchors, detached from its source so they can be kept past this session - or
    /// null unless the scan ran to the end. A cancelled or failed scan covers only part of the
    /// bytes, and anchors for part of a file would reopen as the whole of it.
    /// </summary>
    public FileLineAnchors? DetachAnchors() =>
        IndexingTask.IsCompletedSuccessfully && Failure is null && this.source.LengthSettled
            ? new FileLineAnchors(this.items, LineCount, this.source.AvailableLength)
            : null;

    /// <summary>
    /// The bytes of line <paramref name="lineIndex"/>, which must be below <see cref="LineCount"/>:
    /// found from the nearest anchor (or the last line looked up) before it.
    /// </summary>
    public FileLineSpan GetLineSpan(int lineIndex)
    {
        var coverage = Volatile.Read(ref this.published);
        if ((uint)lineIndex >= (uint)coverage.Lines)
            throw new ArgumentOutOfRangeException(nameof(lineIndex));

        long start = LineStart(lineIndex, coverage.End);
        long end = LineEnd(start, coverage.End);
        return new FileLineSpan(start, checked((int)(end - start)));
    }

    /// <summary>The line holding <paramref name="offset"/>, or null when the lines counted so far
    /// do not reach it.</summary>
    public int? LineAt(long offset)
    {
        long covered = Volatile.Read(ref this.published).End;
        if (offset < 0 || offset >= covered)
            return null;

        var anchor = AnchorAtOrBeforeOffset(offset);
        long position = anchor.Offset;
        for (int line = anchor.Line; ; line++)
        {
            long end = LineEnd(position, covered);
            if (offset < end)
                return line;

            position = end;
        }
    }

    /// <summary>
    /// Waits (asynchronously) for the indexer to reach a target line count
    /// </summary>
    /// <param name="targetCount">number of lines that must be indexed before the task completes</param>
    /// <returns>A task that completes once the index is complete or contains the target number of lines</returns>
    public Task WaitForLineCountAsync(int targetCount) => this.WaitForCountAsync(targetCount);

    /// <summary>
    /// Start the process of indexing the file and returns a container object containing the background indexer
    /// </summary>
    /// <param name="file">Bytes to index; must outlive the index, whose lookups read them</param>
    /// <param name="progressReporter">Progress reporter</param>
    /// <returns>The index class, initially running in the background</returns>
    public static FileOffsetIndex StartIndexing(IByteSource file, IProgressReporter? progressReporter = null, CancellationToken cancellationToken = default)
    {
        var index = new FileOffsetIndex(file);
        index.IndexingTask = index.StartScan(() => index.ProduceAnchors(file, progressReporter, cancellationToken));
        return index;
    }

    /// <summary>
    /// <see cref="Reopen"/> on <paramref name="kept"/> anchors when there are some, else a fresh
    /// scan - the factory a view hands its session.
    /// </summary>
    public static FileOffsetIndex StartIndexing(IByteSource file, FileLineAnchors? kept, IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default)
        => kept is null ? StartIndexing(file, progressReporter, cancellationToken) : Reopen(file, kept);

    private long LineStart(int lineIndex, long covered)
    {
        var anchor = AnchorAtOrBeforeLine(lineIndex);
        int line = anchor.Line;
        long position = anchor.Offset;

        lock (this.walkSync)
        {
            if (this.walkLine <= lineIndex && this.walkLine > line)
                (line, position) = (this.walkLine, this.walkOffset);
        }

        while (line < lineIndex)
        {
            position = LineEnd(position, covered);
            line++;
        }

        lock (this.walkSync)
            (this.walkLine, this.walkOffset) = (lineIndex, position);

        return position;
    }

    /// <summary>One past the newline ending the line that starts at <paramref name="start"/>, or
    /// <paramref name="limit"/> for the last line of a file that does not end in one.</summary>
    private long LineEnd(long start, long limit)
    {
        long position = start;
        while (position < limit)
        {
            var chunk = this.source.GetContiguousSpan(position, (int)Math.Min(WalkChunkSize, limit - position));
            if (chunk.IsEmpty)
                break;

            int newline = chunk.IndexOf((byte)'\n');
            if (newline >= 0)
                return position + newline + 1;

            position += chunk.Length;
        }

        return limit;
    }

    /// <summary>The last anchor for a line at or before <paramref name="line"/>. Anchors are in
    /// line order, and the first is line 0.</summary>
    private FileLineAnchor AnchorAtOrBeforeLine(int line)
    {
        int low = 0, high = this.items.Count - 1, found = 0;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            if (this.items.ItemRef(middle).Line <= line)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return this.items.ItemRef(found);
    }

    /// <summary>The last anchor starting at or before <paramref name="offset"/>.</summary>
    private FileLineAnchor AnchorAtOrBeforeOffset(long offset)
    {
        int low = 0, high = this.items.Count - 1, found = 0;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            if (this.items.ItemRef(middle).Offset <= offset)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return this.items.ItemRef(found);
    }

    /// <summary>
    /// Reads the file, anchoring a line start whenever <see cref="AnchorBytes"/> or
    /// <see cref="AnchorLines"/> have passed, and publishing the line count once per chunk.
    /// Anchors go into the base's append log as they are found, ahead of the count that relies
    /// on them.
    /// </summary>
    /// <param name="file">Memory-mapped file to index</param>
    /// <param name="progressReporter">Allows callers to be notified of progress</param>
    /// <param name="cancellationToken">
    /// Checked once per scan chunk so a caller tearing down the owning source (e.g. window
    /// close mid-scan) can stop this loop before it dereferences memory the OS has unmapped -
    /// see CLAUDE.md / <see cref="MMapFile"/> for why touching a mapping after disposal is a
    /// native use-after-free, not a catchable .NET exception.
    /// </param>
    /// <remarks>Invoked in the background via a task</remarks>
    private void ProduceAnchors(IByteSource file, IProgressReporter? progressReporter, CancellationToken cancellationToken)
    {
        long offset = 0;
        long currentLineStart = 0;
        int lines = 0;
        long anchorOffset = 0;
        int anchorLine = 0;
        this.items.Add(new FileLineAnchor(0, 0));

        try
        {
            // Chunked-scan loop deliberately duplicated (see also SearchSession.Scan,
            // FileTypeDetector): hot path, indirection would cost more than the shared lines.
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // AvailableLength is re-read every turn, never snapshotted: for a streamed
                // source it is only the end *so far*, and a loop that bounded itself by the
                // value it saw at entry would stop there and then publish a complete index over
                // a partial document (see IByteSource.AvailableLength).
                long available = file.AvailableLength;
                if (offset >= available)
                {
                    if (file.LengthSettled)
                        break;

                    file.WaitForLength(offset + 1, cancellationToken);
                    continue;
                }

                // Scan the source's own bytes directly - no buffer, no copy. IndexOf over a byte
                // span is SIMD-vectorized, which is what makes this loop fast on multi-GB files.
                // Whatever length comes back is what this iteration covers: a single-buffer
                // source always serves the whole chunk, and a split one just makes the loop take
                // an extra turn (see IByteSource.GetContiguousSpan).
                var chunk = file.GetContiguousSpan(offset, (int)Math.Min(ScanChunkSize, available - offset));
                if (chunk.IsEmpty)
                    continue;

                int size = chunk.Length;

                int pos = 0;
                while (pos < size)
                {
                    int newlineIndex = chunk.Slice(pos).IndexOf((byte)'\n');
                    if (newlineIndex < 0)
                        break;

                    long lineEndExclusive = offset + pos + newlineIndex + 1;
                    _ = checked((int)(lineEndExclusive - currentLineStart)); // a line's length is an int
                    currentLineStart = lineEndExclusive;
                    lines++;
                    pos += newlineIndex + 1;

                    if (currentLineStart - anchorOffset >= AnchorBytes || lines - anchorLine >= AnchorLines)
                    {
                        this.items.Add(new FileLineAnchor(currentLineStart, lines));
                        (anchorOffset, anchorLine) = (currentLineStart, lines);
                    }
                }

                offset += size;
                Publish(lines, currentLineStart);
                progressReporter?.Report("Indexing", offset, available);
            }
        }
        finally
        {
            // Only on a genuine end of data is there a trailing (newline-less) line to record -
            // and only once the length has settled, since a line with no newline yet may simply
            // be one whose newline has not arrived. On cancellation (close/teardown mid-scan)
            // currentLineStart is wherever the scan stopped, so the remainder is the entire
            // un-scanned tail - which on a multi-GB file exceeds int.MaxValue and overflows the
            // checked cast. Skip it in both cases.
            if (!cancellationToken.IsCancellationRequested && file.LengthSettled && currentLineStart < offset)
            {
                _ = checked((int)(offset - currentLineStart));
                Publish(lines + 1, offset);
            }

            progressReporter?.Report("Indexing", offset, offset);
        }
    }

    private void Publish(int lineCount, long end)
    {
        Volatile.Write(ref this.published, new Coverage(lineCount, end));
        this.OnItemsPublished(lineCount);
    }

    private sealed record Coverage(int Lines, long End)
    {
        public static readonly Coverage None = new(0, 0);
    }
}
