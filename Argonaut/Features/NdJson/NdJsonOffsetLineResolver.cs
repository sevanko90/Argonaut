using System.Threading;
using System.Threading.Tasks;

namespace Argonaut.Features.NdJson;

/// <summary>
/// Maps an absolute byte offset in the file (e.g. a search hit) to the NDJSON line that
/// contains it. Line spans are contiguous from offset 0 and include their trailing newline,
/// so containment is exact; binary search is valid mid-indexing because spans are appended
/// in ascending file order.
///
/// Deliberately a structural twin of JsonOffsetTokenResolver, not a shared generic: the
/// binary search is a hot path and the indirection a generic abstraction would add costs
/// more than the duplicated lines save.
/// </summary>
public static class NdJsonOffsetLineResolver
{
    private const int CoverageWaitBatch = 4096;

    /// <summary>
    /// Resolves <paramref name="offset"/> against the lines indexed so far. Returns null when
    /// the offset isn't covered yet (nothing indexed, or the offset lies beyond the last
    /// indexed line) - use <see cref="ResolveWhenCoveredAsync"/> to wait for coverage.
    /// </summary>
    public static int? ResolveLineForOffset(FileOffsetIndex index, long offset)
    {
        int count = index.LineCount;
        if (count == 0)
            return null;

        // Greatest line starting at or before the offset.
        int lo = 0, hi = count - 1, line = 0;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (index.GetLineSpan(mid).Offset <= offset)
            {
                line = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        var span = index.GetLineSpan(line);
        return offset < span.Offset + span.Length ? line : null;
    }

    /// <summary>
    /// Like <see cref="ResolveLineForOffset"/>, but first waits until indexing has reached
    /// <paramref name="offset"/> (or finished - the final, newline-less line is only appended
    /// at completion).
    /// </summary>
    public static async Task<int?> ResolveWhenCoveredAsync(FileOffsetIndex index, long offset, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            int count = index.LineCount;
            if (index.IsComplete)
                return ResolveLineForOffset(index, offset);

            if (count > 0)
            {
                var last = index.GetLineSpan(count - 1);
                if (offset < last.Offset + last.Length)
                    return ResolveLineForOffset(index, offset);
            }

            // Not cancellable directly, but resolves quickly while indexing is alive (and
            // immediately when it completes), so cancellation is honored between batches.
            await index.WaitForLineCountAsync(count + CoverageWaitBatch);
        }
    }
}
