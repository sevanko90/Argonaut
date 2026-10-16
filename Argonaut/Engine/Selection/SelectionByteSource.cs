using System;
using Argonaut.Engine.Bytes;

namespace Argonaut.Engine.Selection;

/// <summary>
/// Reads a selection's runs end to end as one document, straight from a source over the file -
/// nothing is copied. A view offset is found in the run table by binary search, and a read is
/// cut short at the end of the run it lands in, which the <see cref="IByteSource"/> contract
/// allows: a caller that needs a whole range across runs gathers it with
/// <see cref="CopyTo"/>. The newline a run adds (<see cref="SelectedRun.AddsNewline"/>) is a
/// byte of its own, read from memory.
///
/// One type serves the whole selection and any range of it: <see cref="viewStart"/> and
/// <see cref="viewEnd"/> bound the window, and <see cref="fileBase"/> is where in the file the
/// source being read starts - zero for the whole file, the start of the mapped span for a range.
/// Owns that file source and releases it.
/// </summary>
internal sealed class SelectionByteSource : IByteSource, IDisposable
{
    private static ReadOnlySpan<byte> Newline => "\n"u8;

    private readonly IByteSource file;
    private readonly SelectedRun[] runs;
    private readonly long[] viewStarts;
    private readonly long viewStart;
    private readonly long viewEnd;
    private readonly long fileBase;

    public SelectionByteSource(IByteSource file, SelectedRun[] runs, long[] viewStarts, long viewStart, long viewEnd, long fileBase)
    {
        this.file = file;
        this.runs = runs;
        this.viewStarts = viewStarts;
        this.viewStart = viewStart;
        this.viewEnd = viewEnd;
        this.fileBase = fileBase;
    }

    public long AvailableLength => this.viewEnd - this.viewStart;

    public ReadOnlySpan<byte> GetContiguousSpan(long offset, int maxLength)
    {
        if (offset < 0 || offset >= AvailableLength || maxLength <= 0)
            return ReadOnlySpan<byte>.Empty;

        long view = this.viewStart + offset;
        int i = RunAt(this.viewStarts, view);
        var run = this.runs[i];
        long within = view - this.viewStarts[i];
        if (within >= run.Length)
            return Newline;

        int length = (int)Math.Min(Math.Min(maxLength, run.Length - within), this.viewEnd - view);
        return this.file.GetContiguousSpan(run.FileOffset + within - this.fileBase, length);
    }

    public int CopyTo(long offset, Span<byte> destination)
    {
        int copied = 0;
        while (copied < destination.Length)
        {
            var span = GetContiguousSpan(offset + copied, destination.Length - copied);
            if (span.IsEmpty)
                break;

            span.CopyTo(destination[copied..]);
            copied += span.Length;
        }

        return copied;
    }

    /// <summary>Hints each run's part of the range, so the gaps between them are not read.</summary>
    public void Prefetch(long offset, long length)
    {
        long view = this.viewStart + Math.Max(0, offset);
        long end = Math.Min(this.viewEnd, this.viewStart + offset + length);
        for (int i = view < end ? RunAt(this.viewStarts, view) : this.runs.Length; i < this.runs.Length && this.viewStarts[i] < end; i++)
        {
            var run = this.runs[i];
            long from = Math.Max(view, this.viewStarts[i]) - this.viewStarts[i];
            long to = Math.Min(end - this.viewStarts[i], run.Length);
            if (to > from)
                this.file.Prefetch(run.FileOffset + from - this.fileBase, to - from);
        }
    }

    public void Dispose() => this.file.Release();

    /// <summary>The run holding view offset <paramref name="view"/>: the last that starts at or
    /// before it.</summary>
    internal static int RunAt(long[] viewStarts, long view)
    {
        int found = Array.BinarySearch(viewStarts, view);
        return found >= 0 ? found : ~found - 1;
    }
}
