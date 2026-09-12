using System;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Json;

/// <summary>
/// Addresses the elements of one JSON array by ordinal, so a table view can serve row <c>i</c>
/// without walking the array from the start every time.
///
/// This is the only place in the array-table feature where "a JSON array is not a line-oriented
/// file" lives. A line index is dense - row <c>i</c> is entry <c>i</c> - but a JSON array's
/// <c>i</c>th element sits after every preceding element's whole SUBTREE, found by hopping
/// <see cref="JsonTokenInfo.EndIndex"/>. Left in a row collection that is O(i) per realized row
/// plus a full re-walk on every growth tick, so it moves here.
///
/// Two existing shapes, combined:
///
///  - <b>Sparse anchors, from <see cref="Argonaut.Features.Raw.RawSegmentIndex"/></b>: one stored
///    token index per <see cref="ElementStride"/> elements, and elements are published only at
///    stride boundaries (and once at completion) so every published element's bucket anchor is
///    already visible to a lock-free reader. One <c>int</c> per 64 elements is 640KB on a
///    ten-million-element array, against 40MB for a dense per-element map; the cost is at most
///    63 EndIndex hops per lookup, each an O(1) unpack from the packed token log.
///  - <b>Derived, not scanned, from <see cref="Diff.JsonDiffIndex"/></b>: it consumes another
///    index rather than a file, so it has its own <see cref="IndexingTask"/> and is deliberately
///    NOT an <see cref="IBackgroundIndex"/> - it must never be the thing an
///    <see cref="IndexedSourceSession{TIndex}"/> starts.
///
/// It differs from the diff in one way that matters: the diff waits for its sources to COMPLETE
/// (a half-scanned diff is meaningless), whereas a half-scanned array is a perfectly good table
/// of the elements so far. So this streams, consuming tokens as the source index publishes them.
///
/// There is deliberately no "every element is a scalar, so element i is token i+1" fast path.
/// It would be correct, and it would double the states the walk can be in to save time nobody
/// can see inside a frame.
/// </summary>
public sealed class JsonArrayElementIndex : AppendLogIndexBase<int>
{
    /// <summary>
    /// Elements per stored anchor. Deliberately the same number as
    /// <c>RawSegmentIndex.AnchorStride</c> - no reason for this codebase to hold two answers to
    /// the same RAM/rescan trade.
    /// </summary>
    internal const int ElementStride = 64;

    /// <summary>How many further tokens to wait for when the walk runs out of indexed source
    /// tokens. Matches JsonPathResolver's coverage-wait batch: big enough that a large array
    /// isn't woken once per token, small enough to keep the table growing visibly.</summary>
    private const int CoverageWaitBatch = 4096;

    private readonly JsonStructureIndex source;
    private readonly int arrayTokenIndex;

    // Elements whose own token has CLOSED and whose bucket anchor is published. Written by the
    // walk (release via Volatile.Write), read lock-free by the UI.
    private int publishedElementCount;

    private JsonArrayElementIndex(JsonStructureIndex source, int arrayTokenIndex)
    {
        this.source = source;
        this.arrayTokenIndex = arrayTokenIndex;
    }

    /// <summary>
    /// Completes when the walk stops - the array closed, the source index stopped short, or
    /// cancellation. Faults on failure and on cancellation alike, like every other index here;
    /// <see cref="AppendLogIndexBase{T}.Failure"/> tells the two apart.
    /// </summary>
    public Task IndexingTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Number of elements addressable so far - may grow, in <see cref="ElementStride"/> steps,
    /// until <see cref="AppendLogIndexBase{T}.AllItemsPublished"/> is true. Every counted element's own
    /// token has closed, so <see cref="TokenForElement"/> can hop past it.
    /// </summary>
    public int ElementCount => Volatile.Read(ref this.publishedElementCount);

    /// <summary>
    /// Starts walking the direct children of the array at <paramref name="arrayTokenIndex"/> in
    /// <paramref name="source"/>. The source index must outlive the returned index (the walk
    /// reads it for its whole lifetime - see <c>JsonArrayTableSession</c>).
    /// </summary>
    public static JsonArrayElementIndex Start(JsonStructureIndex source, int arrayTokenIndex, CancellationToken cancellationToken = default)
    {
        var index = new JsonArrayElementIndex(source, arrayTokenIndex);

        index.IndexingTask = index.StartStreamingScan(() => index.WalkAsync(cancellationToken));
        return index;
    }

    /// <summary>
    /// Token index of element <paramref name="elementIndex"/>: one bucket lookup, then at most
    /// <see cref="ElementStride"/> - 1 EndIndex hops. No allocation, no file read.
    /// </summary>
    public int TokenForElement(int elementIndex)
    {
        if ((uint)elementIndex >= (uint)ElementCount)
            throw new ArgumentOutOfRangeException(nameof(elementIndex));

        int bucket = elementIndex / ElementStride;
        int token = this.items.ItemRef(bucket);

        for (int e = bucket * ElementStride; e < elementIndex; e++)
            token = NextElementToken(token);

        return token;
    }

    /// <summary>
    /// Waits for a target element count. Waits on the ANCHOR count that implies it rather than
    /// one anchor at a time - the same reason RawSegmentIndex.WaitForRowCountAsync does: a
    /// caller waiting for a far target against a large array would otherwise be woken, and
    /// allocate a waiter task, once per stride. Completion releases the wait regardless of the
    /// target; the loop is a safety re-check only.
    /// </summary>
    public async Task WaitForElementCountAsync(int targetCount)
    {
        while (ElementCount < targetCount && !AllItemsPublished)
            await WaitForCountAsync((targetCount + ElementStride - 1) / ElementStride + 1);
    }

    /// <summary>
    /// Token index of the element following the one at <paramref name="token"/>. A container is
    /// skipped whole via its EndIndex; a scalar occupies one token.
    ///
    /// EndIndex is -1 until a container closes, and <c>-1 + 1</c> is 0 - a walk that used it
    /// would silently restart from the document root and never terminate. Every element this is
    /// ever called for has been confirmed closed by <see cref="WalkAsync"/> before being counted,
    /// so a -1 here is a broken invariant rather than a race, and says so.
    /// </summary>
    private int NextElementToken(int token)
    {
        var info = this.source.GetToken(token);
        if (!IsContainer(info.Kind))
            return token + 1;

        if (info.EndIndex < 0)
            throw new InvalidOperationException($"Element at token {token} was counted before its container closed.");

        return info.EndIndex + 1;
    }

    private async Task WalkAsync(CancellationToken cancellationToken)
    {
        await this.source.WaitForTokenIndexedAsync(this.arrayTokenIndex);
        cancellationToken.ThrowIfCancellationRequested();

        if (this.source.TokenCount <= this.arrayTokenIndex)
            return; // the source stopped before even the array's own token

        if (this.source.GetToken(this.arrayTokenIndex).Kind != JsonTokenKind.StartArray)
            throw new InvalidOperationException($"Token {this.arrayTokenIndex} is not the start of an array.");

        int elements = 0;
        int token = this.arrayTokenIndex + 1;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!await WaitForTokenAsync(token))
                    return; // source stopped short - keep the elements walked so far

                var info = this.source.GetToken(token);

                // The array's own EndArray token terminates the walk. It cannot be confused
                // with a nested container's End token: containers are skipped whole by
                // EndIndex, so the walk never lands inside one. ParentIndex is the belt to
                // that braces - an End token mirrors its Start token's parent, so our array's
                // closer reports the array's parent, never the array itself.
                if (info.Kind is JsonTokenKind.EndArray or JsonTokenKind.EndObject || info.ParentIndex != this.arrayTokenIndex)
                    return;

                // Advance only over CLOSED elements: EndIndex is -1 while a container is open,
                // and the root array here is open for the entire scan, so "how many tokens are
                // indexed" is never the stopping rule - "did this element's own container
                // close" is.
                if (IsContainer(info.Kind) && !await WaitForCloseAsync(token))
                    return;

                // The anchor goes in before its bucket's elements are counted, so publishing
                // the count of the PREVIOUS buckets here is safe: those elements are closed and
                // their anchors are stored. RawSegmentIndex.AppendAnchor, element-wise.
                if (elements % ElementStride == 0)
                {
                    this.items.Add(token);
                    Volatile.Write(ref this.publishedElementCount, elements);
                    OnItemsPublished(this.items.Count);
                }

                elements++;
                token = NextElementToken(token);
            }
        }
        finally
        {
            // The final publish, without which an array shorter than one stride never crosses a
            // boundary and the table stays empty forever - the common small-array case is the
            // one this exists for. Safe on cancellation too: every counted element was
            // confirmed closed and anchored before being counted.
            Volatile.Write(ref this.publishedElementCount, elements);
        }
    }

    /// <summary>Waits until <paramref name="token"/> is indexed. False if the source stopped
    /// (completed, failed or cancelled) without ever producing it.</summary>
    private async Task<bool> WaitForTokenAsync(int token)
    {
        while (this.source.TokenCount <= token)
        {
            if (this.source.AllItemsPublished)
                return this.source.TokenCount > token;

            await this.source.WaitForTokenIndexedAsync(token);
        }

        return true;
    }

    /// <summary>Waits until the container at <paramref name="token"/> closes. False if the
    /// source stopped without closing it - a truncated or malformed document.</summary>
    private async Task<bool> WaitForCloseAsync(int token)
    {
        while (this.source.GetToken(token).EndIndex < 0)
        {
            if (this.source.AllItemsPublished)
                return this.source.GetToken(token).EndIndex >= 0;

            await this.source.WaitForTokenCountAsync(this.source.TokenCount + CoverageWaitBatch);
        }

        return true;
    }

    private static bool IsContainer(JsonTokenKind kind) => kind is JsonTokenKind.StartObject or JsonTokenKind.StartArray;
}
