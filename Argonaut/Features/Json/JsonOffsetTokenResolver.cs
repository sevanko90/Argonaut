using System;
using System.Threading;
using System.Threading.Tasks;

namespace Argonaut.Features.Json;

/// <summary>
/// Maps an absolute byte offset in the file (e.g. a search hit) back to the JSON token that
/// should be selected for it. Relies on tokens being appended in strictly ascending file
/// order by the streaming indexer, so a binary search over token offsets is valid even while
/// indexing is still running (against a snapshot of TokenCount).
/// </summary>
public static class JsonOffsetTokenResolver
{
    // How many further tokens to wait for per retry when the offset isn't indexed yet. Big
    // enough that a multi-GB coverage wait isn't chatty, small enough to stay responsive.
    private const int CoverageWaitBatch = 4096;

    /// <summary>
    /// Resolves <paramref name="offset"/> against the tokens indexed so far. Returns null
    /// only when nothing is indexed yet. If the offset lies beyond the last indexed token,
    /// it clamps to that token - use <see cref="ResolveWhenCoveredAsync"/> to wait for
    /// coverage first when indexing may still be behind the offset.
    /// </summary>
    public static int? ResolveTokenForOffset(JsonStructureIndex index, long offset)
    {
        int count = index.TokenCount;
        if (count == 0)
            return null;

        // Greatest token whose content starts at or before the offset.
        int lo = 0, hi = count - 1, t = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (index.GetToken(mid).Offset <= offset)
            {
                t = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (t < 0)
            return 0; // before the first token: leading whitespace/BOM

        var token = index.GetToken(t);

        // Inside the token's own content bytes (Max(...,1) covers zero-length content such
        // as an empty string, whose closing quote sits exactly at Offset).
        if (offset < token.Offset + Math.Max(token.Length, 1))
            return t;

        // A string's closing quote sits one past its content - still "this token" to a user.
        if (token.Kind == JsonTokenKind.String && offset == token.Offset + token.Length)
            return t;

        if (t + 1 >= count)
            return t; // past the last indexed token - clamp (callers ensure coverage first)

        // The hit is in the gap between tokens t and t+1: property-name bytes, quotes,
        // punctuation, or whitespace.
        var next = index.GetToken(t + 1);
        if (next.Kind is JsonTokenKind.EndObject or JsonTokenKind.EndArray)
        {
            // Whitespace just before a closing bracket - select the container being closed.
            // If t is a Start token the gap can only be its own (empty) body; otherwise t is
            // a scalar or an End token, and in both cases t's ParentIndex IS the container
            // being closed (End tokens mirror their Start token's parent, see
            // JsonStructureIndex.Build).
            if (token.Kind is JsonTokenKind.StartObject or JsonTokenKind.StartArray)
                return t;
            return Math.Max(token.ParentIndex, 0);
        }

        // Name bytes or punctuation directly ahead of the next value - select that value.
        // (A property name is recorded on the value token that follows it, so a hit inside
        // the name lands here too.)
        return t + 1;
    }

    /// <summary>
    /// Like <see cref="ResolveTokenForOffset"/>, but first waits until indexing has reached
    /// <paramref name="offset"/> (or finished). Needed because a raw byte scan easily outruns
    /// the Utf8JsonReader-based indexer on large files.
    /// </summary>
    public static async Task<int?> ResolveWhenCoveredAsync(JsonStructureIndex index, long offset, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            int count = index.TokenCount;
            if (index.IsComplete || (count > 0 && index.GetToken(count - 1).Offset >= offset))
                return ResolveTokenForOffset(index, offset);

            // Not cancellable directly, but resolves quickly while indexing is alive (and
            // immediately when it completes), so cancellation is honored between batches.
            await index.WaitForTokenCountAsync(count + CoverageWaitBatch);
        }
    }
}
