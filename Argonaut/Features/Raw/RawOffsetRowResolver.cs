using System.Threading;
using System.Threading.Tasks;

namespace Argonaut.Features.Raw;

/// <summary>
/// Maps an absolute byte offset in the file (e.g. a search hit) to the display row that
/// contains it. Segments are contiguous from offset 0, so containment is exact and the row is
/// the smallest segment index whose end offset exceeds the target; binary search is valid
/// mid-indexing because segments are appended in ascending file order.
///
/// Deliberately a structural twin of NdJsonOffsetLineResolver, not a shared generic - same
/// hot-path rationale as documented there.
/// </summary>
public static class RawOffsetRowResolver
{
    private const int CoverageWaitBatch = 4096;

    /// <summary>
    /// Resolves <paramref name="offset"/> against the segments indexed so far. Returns null
    /// when the offset isn't covered yet (nothing indexed, or the offset lies beyond the last
    /// indexed segment) - use <see cref="ResolveWhenCoveredAsync"/> to wait for coverage.
    /// </summary>
    public static int? ResolveRowForOffset(RawSegmentIndex index, long offset)
    {
        int count = index.SegmentCount;
        if (count == 0 || offset < 0 || offset >= index.GetSegmentEnd(count - 1))
            return null;

        // Smallest segment whose (exclusive) end lies beyond the offset.
        int lo = 0, hi = count - 1, row = count - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (index.GetSegmentEnd(mid) > offset)
            {
                row = mid;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return row;
    }

    /// <summary>
    /// Like <see cref="ResolveRowForOffset"/>, but first waits until indexing has reached
    /// <paramref name="offset"/> (or finished).
    /// </summary>
    public static async Task<int?> ResolveWhenCoveredAsync(RawSegmentIndex index, long offset, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            int count = index.SegmentCount;
            if (index.IsComplete)
                return ResolveRowForOffset(index, offset);

            if (count > 0 && offset < index.GetSegmentEnd(count - 1))
                return ResolveRowForOffset(index, offset);

            // Not cancellable directly, but resolves quickly while indexing is alive (and
            // immediately when it completes), so cancellation is honored between batches.
            await index.WaitForSegmentCountAsync(count + CoverageWaitBatch);
        }
    }
}
