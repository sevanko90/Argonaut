using System.Threading;
using System.Threading.Tasks;

namespace Argonaut.Features.Raw;

/// <summary>
/// Maps an absolute byte offset in the file (e.g. a search hit) to the display row that
/// contains it. The lookup itself lives in <see cref="RawSegmentIndex.RowForOffset"/> (binary
/// search over the sparse anchors, then one bucket walk); this adds the wait-for-coverage
/// loop against a still-running indexer, mirroring NdJsonOffsetLineResolver's shape.
/// </summary>
public static class RawOffsetRowResolver
{
    // Rows per coverage re-check while waiting out a scan. Generous on purpose: each
    // iteration costs a waiter allocation plus a PublishedEndOffset bucket walk, and at
    // ~64K rows (≈ a few MB of scan) the extra reveal latency is milliseconds.
    private const int CoverageWaitBatch = 64 * 1024;

    /// <summary>
    /// Resolves <paramref name="offset"/> against the rows published so far. Returns null
    /// when the offset isn't covered yet (nothing published, or the offset lies beyond the
    /// last published row) - use <see cref="ResolveWhenCoveredAsync"/> to wait for coverage.
    /// </summary>
    public static int? ResolveRowForOffset(RawSegmentIndex index, long offset)
        => index.RowForOffset(offset);

    /// <summary>
    /// Like <see cref="ResolveRowForOffset"/>, but first waits until indexing has reached
    /// <paramref name="offset"/> (or finished).
    /// </summary>
    public static async Task<int?> ResolveWhenCoveredAsync(RawSegmentIndex index, long offset, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            int count = index.RowCount;
            if (index.IsComplete)
                return index.RowForOffset(offset);

            if (count > 0 && offset < index.PublishedEndOffset)
                return index.RowForOffset(offset);

            // Not cancellable directly, but resolves quickly while indexing is alive (and
            // immediately when it completes), so cancellation is honored between batches.
            await index.WaitForRowCountAsync(count + CoverageWaitBatch);
        }
    }
}
