using System.Threading;
using System.Threading.Tasks;

namespace Argonaut.Engine.Indexing.Lines;

/// <summary>
/// Maps an absolute byte offset in the file (e.g. a search hit) to the line that contains it,
/// for any view over a <see cref="FileOffsetIndex"/> (NDJSON, CSV). Line spans are contiguous
/// from offset 0 and include their trailing newline, so containment is exact.
/// </summary>
public static class OffsetLineResolver
{
    private const int CoverageWaitBatch = 4096;

    /// <summary>
    /// Resolves <paramref name="offset"/> against the lines indexed so far. Returns null when
    /// the offset isn't covered yet (nothing indexed, or the offset lies beyond the last
    /// indexed line) - use <see cref="ResolveWhenCoveredAsync"/> to wait for coverage.
    /// </summary>
    public static int? ResolveLineForOffset(FileOffsetIndex index, long offset) => index.LineAt(offset);

    /// <summary>
    /// Like <see cref="ResolveLineForOffset"/>, but first waits until indexing has reached
    /// <paramref name="offset"/> (or finished - the final, newline-less line is only counted
    /// at completion).
    /// </summary>
    public static async Task<int?> ResolveWhenCoveredAsync(FileOffsetIndex index, long offset, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            int count = index.LineCount;
            if (index.AllItemsPublished || offset < index.CoveredLength)
                return index.LineAt(offset);

            // Not cancellable directly, but resolves quickly while indexing is alive (and
            // immediately when it completes), so cancellation is honored between batches.
            await index.WaitForLineCountAsync(count + CoverageWaitBatch);
        }
    }
}
