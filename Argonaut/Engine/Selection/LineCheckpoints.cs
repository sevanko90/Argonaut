using System;
using System.Collections.Generic;

namespace Argonaut.Engine.Selection;

/// <summary>
/// How many newlines come before each scan chunk's start, for the stretch of a file a
/// <see cref="LineSelectionLocator"/> pass has read - so the next pass over the same file can
/// jump to the chunk a line is in instead of counting from the start again. What makes following
/// a live filter file cheap: a reload costs a chunk or two around each range, not a rescan.
///
/// One per file, used by one pass at a time. Valid only while the file's bytes before the last
/// checkpoint do not change; a file that is rewritten needs a new one.
/// </summary>
public sealed class LineCheckpoints
{
    // Parallel and sorted: offsets ascend, and so do the newline counts with them.
    private readonly List<long> offsets = [0];
    private readonly List<long> counts = [0];

    /// <summary>How many chunk starts are known.</summary>
    public int Count => this.offsets.Count;

    /// <summary>Records that <paramref name="newlinesBefore"/> newlines come before
    /// <paramref name="offset"/> - only past the last one known, so the list stays sorted.</summary>
    internal void Record(long offset, long newlinesBefore)
    {
        if (offset <= this.offsets[^1])
            return;

        this.offsets.Add(offset);
        this.counts.Add(newlinesBefore);
    }

    /// <summary>The furthest known chunk start with fewer than <paramref name="newlines"/>
    /// newlines before it - so the newline-th newline is at or after it, and a scan for it can
    /// begin there. Fewer, not as many: with that many before it, the newline itself could be.</summary>
    internal (long Offset, long NewlinesBefore) Before(long newlines)
    {
        // The first chunk start with at least that many, less one. Index 0 has none before it.
        int lo = 0, hi = this.counts.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (this.counts[mid] < newlines)
                lo = mid + 1;
            else
                hi = mid;
        }

        int i = Math.Max(0, lo - 1);
        return (this.offsets[i], this.counts[i]);
    }
}
