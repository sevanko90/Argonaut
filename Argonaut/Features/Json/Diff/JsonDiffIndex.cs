using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Engine.Indexing;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Engine.Progress;
using Argonaut.Features.Json.Indexing;

namespace Argonaut.Features.Json.Diff;

/// <summary>How one merged node differs between the two documents.</summary>
public enum DiffStatus
{
    Unchanged,
    Added,
    Removed,
    Modified,
    Moved
}

/// <summary>
/// One decoded diff record - the unpacked, reader-facing shape of
/// <see cref="JsonDiffIndex.PackedDiffRecord"/>. What each kind of record covers is laid out in
/// docs/json-diff-merged-tree-plan.md: a record is a run of unchanged sibling pairs, a descended
/// pair of containers, a changed or one-sided node, one end of a move, or a range given up on.
/// </summary>
/// <param name="Index">This record's index in the diff log.</param>
/// <param name="Left">The (first) node the record covers in the left document, or
/// <see cref="JsonDiffNode.Absent"/>. For a cross-parent move, the source node on both ends.</param>
/// <param name="Right">Likewise in the right document; the destination node on both ends of a
/// cross-parent move.</param>
/// <param name="Status">How this node differs. A run is <see cref="DiffStatus.Unchanged"/>.</param>
/// <param name="Depth">Merged-tree nesting depth (0 at the top).</param>
/// <param name="ParentRecord">Record index of the enclosing descended pair, or -1 at the top.</param>
/// <param name="SubtreeEnd">Exclusive end of this record's descendant records; <c>Index + 1</c>
/// for a record without descendants, -1 while the descent below it is still streaming.</param>
/// <param name="IsMoveSource">For a cross-parent <see cref="DiffStatus.Moved"/> pair: true on the
/// record at the old position (the stub), false at the new one.</param>
/// <param name="MovePartnerRecord">For a cross-parent move: the record index of the other end;
/// -1 otherwise.</param>
/// <param name="LeftOrdinal">Where <see cref="Left"/> sits among its parent's children, from 0;
/// -1 when absent. For an in-array move this is the element's source position - what the "moved
/// from [n]" badge shows.</param>
/// <param name="RightOrdinal">Likewise on the right.</param>
/// <param name="LeftCount">How many consecutive left siblings the record covers from
/// <see cref="Left"/>: 1 for a node, the run's length, a range's left side; 0 when absent.</param>
/// <param name="RightCount">Likewise on the right.</param>
/// <param name="LeftEnd">One past the last left byte the record covers; -1 when absent.</param>
/// <param name="RightEnd">Likewise on the right.</param>
/// <param name="LeftAnchor">Where in the left document this record sits in merged order: its own
/// left row start where it stands, otherwise the end of the left node before it. Never decreases
/// along the log - what a left offset or a scroll position finds a record by.</param>
/// <param name="IsAlignmentApproximate">An array pair whose middle was too long to align, so it was
/// compared element by element in place.</param>
/// <param name="IsRange">The part of such a middle past the record budget: left and right
/// elements shown whole, not compared.</param>
/// <param name="IsMovedWithin">A descended array element that changed and also moved within its
/// array, paired by identity key; <see cref="LeftOrdinal"/> is where it came from.</param>
/// <param name="FirstChild">For the destination of a move whose content changed - paired by
/// similarity after the descent - where its children begin in the log, which is after the end of
/// the descent rather than straight after it; -1 otherwise.</param>
/// <param name="ChildrenEnd">With <see cref="FirstChild"/>, the exclusive end of those children.</param>
/// <param name="LeftDepth">Document depth of <see cref="Left"/> - the merged depth, except under a
/// move across parents, whose two ends sit at different depths.</param>
/// <param name="RightDepth">Likewise of <see cref="Right"/>.</param>
public readonly record struct JsonDiffRecord(
    int Index,
    JsonDiffNode Left,
    JsonDiffNode Right,
    DiffStatus Status,
    int Depth,
    int ParentRecord,
    int SubtreeEnd,
    bool IsMoveSource,
    int MovePartnerRecord,
    long LeftOrdinal,
    long RightOrdinal,
    long LeftCount,
    long RightCount,
    long LeftEnd,
    long RightEnd,
    long LeftAnchor,
    bool IsAlignmentApproximate,
    bool IsRange,
    bool IsMovedWithin,
    int FirstChild = -1,
    int ChildrenEnd = -1,
    int LeftDepth = 0,
    int RightDepth = 0)
{
    /// <summary>A run of unchanged sibling pairs - drawn row by row, with no row of its own.</summary>
    public bool IsRun => Status == DiffStatus.Unchanged;

    /// <summary>Whether the record has child records - a pair of containers that was descended
    /// into: the records after it up to <see cref="SubtreeEnd"/>, or those from
    /// <see cref="FirstChild"/>.</summary>
    public bool HasChildRecords => FirstChild >= 0 || SubtreeEnd < 0 || SubtreeEnd > Index + 1;

    /// <summary>One end of a move across parents, as opposed to a move within an array.</summary>
    public bool IsCrossParentMove => Status == DiffStatus.Moved && MovePartnerRecord >= 0;
}

/// <summary>
/// The headless semantic differ: compares two fully indexed JSON documents by Merkle content hash
/// (see <see cref="JsonContentHashes"/>: recorded for large containers, read from the bytes for
/// everything else) and publishes records in merged render order - the record log is the
/// flattened diff tree the merged cursor walks. Same publishing shape as the other scanners
/// (<see cref="AppendLogIndexBase{T}"/>), so it gets AllItemsPublished/Failure/waiters and
/// lock-free reads for free.
///
/// Key properties, each load-bearing:
///
///  - Equal hashes never descend, and consecutive equal siblings are one run record - so the log
///    grows with the number of differences, not with the size of the documents or the width of a
///    changed level.
///  - Children match by identity (object: decoded name; array: anchored hash, or an identity key
///    member), never by position, so index shifts cannot produce spurious differences.
///  - A changed level is trimmed first: its common prefix and suffix are streamed, holding
///    nothing, and only the middle is aligned - and only the middle is capped.
///  - Added/Removed subtrees are emitted whole (one record, no descent); the cross-parent
///    move pass over those records is therefore bounded by the size of the change.
///  - Fields that mutate after publication (the move pass rewrites a pair of records; a
///    container's SubtreeEnd and flags finalize after its descent) are published then mutated
///    with Volatile, StatusBits the release/acquire gate (written last, read first).
///
/// The diff runs on its own dedicated thread with an oversized stack: the descent recurses per
/// nesting level, which could overflow a default 1MB task stack.
/// </summary>
public sealed class JsonDiffIndex : AppendLogIndexBase<JsonDiffIndex.PackedDiffRecord>
{
    /// <summary>The most elements either side of an array's middle - what is left once its common
    /// prefix and suffix are trimmed - that are aligned. Alignment holds both sides' element hashes
    /// in memory; a longer middle is compared in place instead (see
    /// <see cref="MaxPositionalRecords"/>) and flagged <see cref="JsonDiffRecord.IsAlignmentApproximate"/>.</summary>
    public const int MaxAlignableArrayElements = 100_000;

    /// <summary>How many records an in-place comparison of an over-cap middle may emit before the
    /// rest of the middle becomes one <see cref="JsonDiffRecord.IsRange"/> record - so a middle
    /// that differs everywhere costs one record past this, not one per element.</summary>
    public const int MaxPositionalRecords = 100_000;

    // Myers inside inter-anchor gaps gives up past this many edit steps and falls back to
    // positional pairing - keeps a pathological gap O(gap * MaxMyersEditDistance) instead
    // of quadratic.
    private const int MaxMyersEditDistance = 512;

    /// <summary>At most this many identity-key candidates are tried per array.</summary>
    private const int MaxIdentityCandidates = 4;

    /// <summary>The similarity pass is skipped when the removed and added containers left over
    /// from exact pairing would make more pairs than this - a diff that degenerate stays
    /// added/removed rather than going quadratic.</summary>
    public const int MaxSimilarityPairs = 1000;

    /// <summary>A container with more children than this is not scored for similarity.</summary>
    private const int MaxSimilarityChildren = 10_000;

    /// <summary>The share of direct children two containers must have in common to be paired as
    /// one that moved and changed.</summary>
    private const double SimilarityThreshold = 0.5;

    private const int StatusMask = 0x7;
    private const int FlagMoveSource = 1 << 3;
    private const int FlagApproximate = 1 << 4;
    private const int FlagCrossParentMove = 1 << 5;
    private const int FlagRange = 1 << 6;
    private const int FlagMovedWithin = 1 << 7;

    /// <summary>
    /// Compact stored form of one <see cref="JsonDiffRecord"/>. StatusBits carries the
    /// <see cref="DiffStatus"/> in its low bits plus the flag bits above. StatusBits, the
    /// node offsets, ordinals, ends, MovePartnerRecord and SubtreeEnd may be mutated after
    /// publication (move reconciliation / descent finalization) and are accessed with
    /// Volatile on both sides; StatusBits is always written LAST and read FIRST, so a
    /// reader that observes a mutated status also observes the partner fields that came
    /// with it. Public only because it parameterizes the base class.
    /// </summary>
    public struct PackedDiffRecord
    {
        public long LeftRowStart;
        public long LeftValueStart;
        public long RightRowStart;
        public long RightValueStart;
        public long LeftOrdinal;
        public long RightOrdinal;
        public long LeftCount;
        public long RightCount;
        public long LeftEnd;
        public long RightEnd;
        public long LeftAnchor;
        public int ParentRecord;
        public int SubtreeEnd;
        public int StatusBits;
        public int MovePartnerRecord;
        public int FirstChild;
        public int ChildrenEnd;
        public ushort Depth;
        public ushort LeftDepth;
        public ushort RightDepth;
    }

    /// <summary>
    /// One level of the merged tree as it is emitted: where its records go, where in the left
    /// document emission has got to (the anchor for anything right-only), and the run of
    /// unchanged pairs being gathered. Dropped when the level is done.
    /// </summary>
    private sealed class Level(int depth, int parentRecord, long leftPosition, long anchorOverride, int leftDepth, int rightDepth)
    {
        public int Depth { get; } = depth;

        /// <summary>Document depth of this level's nodes on each side - the merged depth, except
        /// beneath a move across parents.</summary>
        public int LeftDepth { get; } = leftDepth;

        public int RightDepth { get; } = rightDepth;

        public int ParentRecord { get; } = parentRecord;

        /// <summary>The end of the last left node passed at this level.</summary>
        public long LeftPosition { get; set; } = leftPosition;

        /// <summary>Under an element that moved within its array its left nodes are elsewhere, so
        /// every record beneath it takes its anchor, keeping anchors in order; -1 otherwise.</summary>
        public long AnchorOverride { get; } = anchorOverride;

        public bool RunOpen;
        public TreeNode RunLeft;
        public TreeNode RunRight;
        public long RunLeftOrdinal;
        public long RunRightOrdinal;
        public long RunCount;
        public long RunLeftEnd;
        public long RunRightEnd;

        public long AnchorAt(long leftRowStart) => AnchorOverride >= 0 ? AnchorOverride : leftRowStart;
    }

    private readonly JsonDiffDocument left;
    private readonly JsonDiffDocument right;
    private readonly IProgressReporter? progressReporter;
    private readonly CancellationToken cancellationToken;

    // Removed/Added *container* records bucketed by content hash for the cross-parent move
    // pass. Populated during the descent (only whole-subtree records land here, so this is
    // bounded by the size of the change), consumed once after it.
    private readonly Dictionary<ulong, (int RecordIndex, int Count)> removedContainersByHash = new();
    private readonly Dictionary<ulong, (int RecordIndex, int Count)> addedContainersByHash = new();
    private readonly List<int> removedContainers = new();
    private readonly List<int> addedContainers = new();
    private int mainRecordCount = -1;

    private long progressLength;
    private long nextProgressReport;
    private int ticks;

    public Task IndexingTask { get; private set; } = Task.CompletedTask;

    public int RecordCount => this.ItemCount;

    /// <summary>The records the descent emitted, in merged order; the children of moves paired by
    /// similarity follow them. All of them while the descent is still running.</summary>
    public int MainRecordCount => Volatile.Read(ref this.mainRecordCount) is var main and >= 0 ? main : this.ItemCount;

    public Task WaitForRecordCountAsync(int targetCount) => this.WaitForCountAsync(targetCount);

    private JsonDiffIndex(JsonDiffDocument left, JsonDiffDocument right,
        IProgressReporter? progressReporter, CancellationToken cancellationToken)
    {
        this.left = left;
        this.right = right;
        this.progressReporter = progressReporter;
        this.cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Starts the diff worker over two sessions indexed with content hashes
    /// (<see cref="JsonSparseIndex.StartIndexingWithContentHashes(Argonaut.Engine.Bytes.IByteSource, IProgressReporter?, CancellationToken)"/>).
    /// It first waits for BOTH indexes to finish (recorded hashes are only final once every
    /// container closes); if either side fails or is cancelled the diff completes empty - side
    /// failures are the caller's to attribute and report. The caller (JsonDiffSession)
    /// guarantees both mappings outlive <see cref="IndexingTask"/>.
    /// </summary>
    public static JsonDiffIndex Start(IndexedSourceSession<JsonSparseIndex> left, IndexedSourceSession<JsonSparseIndex> right,
        IProgressReporter? progressReporter = null, CancellationToken cancellationToken = default)
    {
        // Its own readers: the view reads the same documents on the UI thread meanwhile.
        var diff = new JsonDiffIndex(new JsonDiffDocument(left), new JsonDiffDocument(right), progressReporter, cancellationToken);

        // A dedicated thread with an oversized stack instead of Task.Run: the descent
        // recurses per nesting level, which does not fit a default 1MB pool-thread stack. TCS mirrors Task.Run's completion semantics.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                diff.RunIndexing(diff.Run);
                tcs.TrySetResult();
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, maxStackSize: 32 << 20)
        {
            IsBackground = true,
            Name = "json-diff"
        };

        diff.IndexingTask = tcs.Task;
        thread.Start();
        return diff;
    }

    public JsonDiffRecord GetRecord(int index)
    {
        ref var packed = ref this.items.ItemRef(index);

        // StatusBits is the acquire gate: the move pass writes partner fields first and
        // status last, so reading status first guarantees the partner fields it implies.
        int statusBits = Volatile.Read(ref packed.StatusBits);

        return new JsonDiffRecord(
            index,
            new JsonDiffNode(Volatile.Read(ref packed.LeftRowStart), Volatile.Read(ref packed.LeftValueStart)),
            new JsonDiffNode(Volatile.Read(ref packed.RightRowStart), Volatile.Read(ref packed.RightValueStart)),
            (DiffStatus)(statusBits & StatusMask),
            packed.Depth,
            packed.ParentRecord,
            Volatile.Read(ref packed.SubtreeEnd),
            (statusBits & FlagMoveSource) != 0,
            Volatile.Read(ref packed.MovePartnerRecord),
            Volatile.Read(ref packed.LeftOrdinal),
            Volatile.Read(ref packed.RightOrdinal),
            packed.LeftCount,
            packed.RightCount,
            Volatile.Read(ref packed.LeftEnd),
            Volatile.Read(ref packed.RightEnd),
            packed.LeftAnchor,
            (statusBits & FlagApproximate) != 0,
            (statusBits & FlagRange) != 0,
            (statusBits & FlagMovedWithin) != 0,
            Volatile.Read(ref packed.FirstChild),
            Volatile.Read(ref packed.ChildrenEnd),
            Volatile.Read(ref packed.LeftDepth),
            Volatile.Read(ref packed.RightDepth));
    }

    // ── The worker ─────────────────────────────────────────────────────────────────────

    private void Run()
    {
        // Both sides must be fully indexed before recorded hashes are final. A faulted or
        // cancelled side means there is nothing to diff - complete empty; the session/view
        // model attributes the side failure.
        try
        {
            Task.WaitAll(new[] { this.left.Index.IndexingTask, this.right.Index.IndexingTask }, this.cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return;
        }

        if (this.left.Index.ContentHashes is null || this.right.Index.ContentHashes is null)
            throw new InvalidOperationException("Diffing requires both indexes to be built with content hashes.");

        var leftRoot = this.left.Root;
        var rightRoot = this.right.Root;
        if (leftRoot is null && rightRoot is null)
            return;

        this.progressLength = Math.Max(1, this.left.Bytes.AvailableLength);
        this.nextProgressReport = 0;

        var top = new Level(depth: 0, parentRecord: -1, leftPosition: 0, anchorOverride: -1, leftDepth: 0, rightDepth: 0);
        if (leftRoot is not { } leftTop)
            EmitAdded(top, rightRoot!.Value, 0);
        else if (rightRoot is not { } rightTop)
            EmitRemoved(top, leftTop, 0);
        else
            DiffPair(top, leftTop, rightTop, 0, 0, movedWithin: false);

        FlushRun(top);
        Volatile.Write(ref this.mainRecordCount, this.items.Count);
        ReconcileCrossParentMoves();
        PairSimilarContainers();

        this.progressReporter?.Report("Comparing", this.progressLength, this.progressLength);
    }

    /// <summary>Checks for cancellation and reports progress now and then - from the loops that
    /// stream long unchanged stretches without emitting anything, as well as from emission.</summary>
    private void Tick(long leftPosition)
    {
        if ((++this.ticks & 0xFFF) != 0)
            return;

        this.cancellationToken.ThrowIfCancellationRequested();

        // Records follow the left document's order, so where the comparison stands in it is how
        // far it has come.
        if (leftPosition >= this.nextProgressReport)
        {
            this.progressReporter?.Report("Comparing", Math.Min(leftPosition, this.progressLength - 1), this.progressLength);
            this.nextProgressReport = leftPosition + (1 << 20);
        }
    }

    private int Emit(Level level, JsonDiffNode leftNode, JsonDiffNode rightNode, DiffStatus status,
        long leftOrdinal, long rightOrdinal, long leftCount, long rightCount, long leftEnd, long rightEnd,
        long anchor, int flags = 0, int subtreeEnd = 0)
    {
        int index = this.items.Count;
        this.items.Add(new PackedDiffRecord
        {
            LeftRowStart = leftNode.RowStart,
            LeftValueStart = leftNode.ValueStart,
            RightRowStart = rightNode.RowStart,
            RightValueStart = rightNode.ValueStart,
            LeftOrdinal = leftOrdinal,
            RightOrdinal = rightOrdinal,
            LeftCount = leftCount,
            RightCount = rightCount,
            LeftEnd = leftEnd,
            RightEnd = rightEnd,
            LeftAnchor = anchor,
            ParentRecord = level.ParentRecord,
            SubtreeEnd = subtreeEnd == 0 ? index + 1 : subtreeEnd,
            StatusBits = (int)status | flags,
            MovePartnerRecord = -1,
            FirstChild = -1,
            ChildrenEnd = -1,
            Depth = (ushort)level.Depth,
            LeftDepth = (ushort)level.LeftDepth,
            RightDepth = (ushort)level.RightDepth,
        });
        this.OnItemsPublished(index + 1);
        Tick(anchor);
        return index;
    }

    // ── Emission at one level ──────────────────────────────────────────────────────────

    /// <summary>
    /// Adds <paramref name="count"/> consecutive unchanged pairs to the level's run, starting at
    /// <paramref name="firstLeft"/>/<paramref name="firstRight"/>. The run carries on while both
    /// ordinals follow on from it; anything else closes it and starts another.
    /// </summary>
    private void ExtendRun(Level level, TreeNode firstLeft, TreeNode firstRight, long leftOrdinal, long rightOrdinal,
        long count, long lastLeftEnd, long lastRightEnd)
    {
        if (!level.RunOpen
            || leftOrdinal != level.RunLeftOrdinal + level.RunCount
            || rightOrdinal != level.RunRightOrdinal + level.RunCount)
        {
            FlushRun(level);
            level.RunOpen = true;
            level.RunLeft = firstLeft;
            level.RunRight = firstRight;
            level.RunLeftOrdinal = leftOrdinal;
            level.RunRightOrdinal = rightOrdinal;
            level.RunCount = 0;
        }

        level.RunCount += count;
        level.RunLeftEnd = lastLeftEnd;
        level.RunRightEnd = lastRightEnd;
        level.LeftPosition = lastLeftEnd;
    }

    private void FlushRun(Level level)
    {
        if (!level.RunOpen)
            return;

        level.RunOpen = false;
        Emit(level, JsonDiffNode.Of(level.RunLeft), JsonDiffNode.Of(level.RunRight), DiffStatus.Unchanged,
            level.RunLeftOrdinal, level.RunRightOrdinal, level.RunCount, level.RunCount,
            level.RunLeftEnd, level.RunRightEnd, level.AnchorAt(level.RunLeft.RowStart));
    }

    private void EmitRemoved(Level level, TreeNode leftNode, long leftOrdinal)
    {
        FlushRun(level);
        long end = this.left.End(leftNode);
        int record = Emit(level, JsonDiffNode.Of(leftNode), JsonDiffNode.Absent, DiffStatus.Removed,
            leftOrdinal, -1, 1, 0, end, -1, level.AnchorAt(leftNode.RowStart));
        level.LeftPosition = end;
        if (leftNode.IsContainer)
        {
            RegisterMoveCandidate(this.removedContainersByHash, this.left.Hash(leftNode), record);
            this.removedContainers.Add(record);
        }
    }

    private void EmitAdded(Level level, TreeNode rightNode, long rightOrdinal)
    {
        FlushRun(level);
        int record = Emit(level, JsonDiffNode.Absent, JsonDiffNode.Of(rightNode), DiffStatus.Added,
            -1, rightOrdinal, 0, 1, -1, this.right.End(rightNode), level.AnchorAt(level.LeftPosition));
        if (rightNode.IsContainer)
        {
            RegisterMoveCandidate(this.addedContainersByHash, this.right.Hash(rightNode), record);
            this.addedContainers.Add(record);
        }
    }

    /// <summary>An unchanged element that moved within its array: shown at its new position,
    /// badged with where it came from.</summary>
    private void EmitMovedIn(Level level, TreeNode leftNode, TreeNode rightNode, long leftOrdinal, long rightOrdinal)
    {
        FlushRun(level);
        Emit(level, JsonDiffNode.Of(leftNode), JsonDiffNode.Of(rightNode), DiffStatus.Moved,
            leftOrdinal, rightOrdinal, 1, 1, this.left.End(leftNode), this.right.End(rightNode), level.AnchorAt(level.LeftPosition));
    }

    private static void RegisterMoveCandidate(Dictionary<ulong, (int RecordIndex, int Count)> bucket, ulong hash, int record)
    {
        bucket[hash] = bucket.TryGetValue(hash, out var existing)
            ? (existing.RecordIndex, existing.Count + 1)
            : (record, 1);
    }

    /// <summary>
    /// Diffs one matched node pair. Equal hashes join the level's run - the Merkle
    /// short-circuit. Same-kind containers with differing hashes descend; every other
    /// combination is an undescended Modified record (the panes each render their own side).
    /// <paramref name="movedWithin"/> marks an array element paired by identity key out of
    /// order: unchanged, it is a move; changed, a descended pair flagged as moved.
    /// </summary>
    private void DiffPair(Level level, TreeNode leftNode, TreeNode rightNode, long leftOrdinal, long rightOrdinal, bool movedWithin)
    {
        long leftEnd = this.left.End(leftNode);
        long rightEnd = this.right.End(rightNode);

        if (this.left.Hash(leftNode) == this.right.Hash(rightNode))
        {
            if (movedWithin)
                EmitMovedIn(level, leftNode, rightNode, leftOrdinal, rightOrdinal);
            else
                ExtendRun(level, leftNode, rightNode, leftOrdinal, rightOrdinal, 1, leftEnd, rightEnd);
            return;
        }

        FlushRun(level);
        long anchor = level.AnchorAt(movedWithin ? level.LeftPosition : leftNode.RowStart);
        int moved = movedWithin ? FlagMovedWithin : 0;
        var leftAt = JsonDiffNode.Of(leftNode);
        var rightAt = JsonDiffNode.Of(rightNode);

        if (leftNode.FormatKind != rightNode.FormatKind || !leftNode.IsContainer)
        {
            Emit(level, leftAt, rightAt, DiffStatus.Modified, leftOrdinal, rightOrdinal, 1, 1, leftEnd, rightEnd, anchor, moved);
        }
        else
        {
            int record = Emit(level, leftAt, rightAt, DiffStatus.Modified, leftOrdinal, rightOrdinal, 1, 1, leftEnd, rightEnd,
                anchor, moved, subtreeEnd: -1);

            var children = new Level(level.Depth + 1, record, this.left.Reader.FirstChildPosition(leftNode.ValueStart),
                movedWithin ? anchor : level.AnchorOverride, level.LeftDepth + 1, level.RightDepth + 1);
            bool approximate = DiffChildren(children, leftNode, rightNode);

            ref var packed = ref this.items.ItemRef(record);
            if (approximate)
                Volatile.Write(ref packed.StatusBits, packed.StatusBits | FlagApproximate);
            Volatile.Write(ref packed.SubtreeEnd, this.items.Count);
        }

        if (!movedWithin)
            level.LeftPosition = leftEnd;
    }

    // ── A changed level ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Emits the children of a pair of same-kind containers whose hashes differ: the common
    /// prefix and suffix streamed as runs, the middle between them aligned - or, for an array
    /// middle over <see cref="MaxAlignableArrayElements"/>, compared in place. True when it was
    /// compared in place (the container is flagged approximate).
    /// </summary>
    private bool DiffChildren(Level level, TreeNode leftContainer, TreeNode rightContainer)
    {
        bool isObject = leftContainer.FormatKind == (byte)JsonTokenKind.StartObject;
        long leftCount = this.left.ChildCount(leftContainer);
        long rightCount = this.right.ChildCount(rightContainer);
        long common = Math.Min(leftCount, rightCount);

        // Prefix: in step from the start while the pairs are the same.
        long prefix = 0;
        using (var leftChildren = this.left.ChildrenFrom(leftContainer, 0).GetEnumerator())
        using (var rightChildren = this.right.ChildrenFrom(rightContainer, 0).GetEnumerator())
        {
            while (prefix < common && leftChildren.MoveNext() && rightChildren.MoveNext())
            {
                var leftChild = leftChildren.Current;
                var rightChild = rightChildren.Current;
                if (!Same(isObject, leftChild, rightChild))
                    break;

                ExtendRun(level, leftChild, rightChild, prefix, prefix, 1, this.left.End(leftChild), this.right.End(rightChild));
                Tick(leftChild.RowStart);
                prefix++;
            }
        }

        // Suffix: in step back from both ends, no further than the prefix reached.
        long suffix = 0;
        TreeNode suffixLeft = default, suffixRight = default;
        long suffixLeftEnd = -1, suffixRightEnd = -1;
        using (var leftChildren = this.left.ChildrenBackward(leftContainer, leftCount).GetEnumerator())
        using (var rightChildren = this.right.ChildrenBackward(rightContainer, rightCount).GetEnumerator())
        {
            while (suffix < common - prefix && leftChildren.MoveNext() && rightChildren.MoveNext())
            {
                var leftChild = leftChildren.Current;
                var rightChild = rightChildren.Current;
                if (!Same(isObject, leftChild, rightChild))
                    break;

                if (suffix == 0)
                {
                    suffixLeftEnd = this.left.End(leftChild);
                    suffixRightEnd = this.right.End(rightChild);
                }

                suffixLeft = leftChild;
                suffixRight = rightChild;
                Tick(leftChild.RowStart);
                suffix++;
            }
        }

        long leftMiddle = leftCount - prefix - suffix;
        long rightMiddle = rightCount - prefix - suffix;
        bool approximate = false;
        if (leftMiddle > 0 || rightMiddle > 0)
        {
            if (isObject)
            {
                DiffObjectMiddle(level, leftContainer, rightContainer, prefix, leftMiddle, rightMiddle);
            }
            else if (leftMiddle <= MaxAlignableArrayElements && rightMiddle <= MaxAlignableArrayElements)
            {
                DiffArrayMiddle(level, leftContainer, rightContainer, prefix, (int)leftMiddle, (int)rightMiddle);
            }
            else
            {
                DiffArrayInPlace(level, leftContainer, rightContainer, prefix, leftMiddle, rightMiddle);
                approximate = true;
            }
        }

        if (suffix > 0)
        {
            ExtendRun(level, suffixLeft, suffixRight, leftCount - suffix, rightCount - suffix, suffix, suffixLeftEnd, suffixRightEnd);
        }

        FlushRun(level);
        return approximate;
    }

    /// <summary>Whether two children in the same place are the same: equal content, and for an
    /// object's members the same decoded name.</summary>
    private bool Same(bool isObject, TreeNode leftChild, TreeNode rightChild)
    {
        if (isObject && !JsonUnescape.DecodedEquals(this.left.Text.NameBytes(leftChild), this.right.Text.NameBytes(rightChild)))
            return false;

        return this.left.Hash(leftChild) == this.right.Hash(rightChild);
    }

    private static List<TreeNode> Collect(JsonDiffDocument document, TreeNode container, long first, long count)
    {
        var nodes = new List<TreeNode>((int)Math.Min(count, 1 << 16));
        foreach (var child in document.ChildrenFrom(container, first))
        {
            if (nodes.Count == count)
                break;
            nodes.Add(child);
        }

        return nodes;
    }

    // ── Objects ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Matches the middle of one object level by decoded property name (hash first, byte-verify on
    /// match to guard against name-hash collisions), then emits in merged key order: the
    /// left document's order is the base, right-only keys slot in at their relative
    /// position from the right. All per-level state is dropped on return - memory is
    /// the width of the changed middle, never the document.
    /// </summary>
    private void DiffObjectMiddle(Level level, TreeNode leftContainer, TreeNode rightContainer, long first, long leftCount, long rightCount)
    {
        var leftChildren = Collect(this.left, leftContainer, first, leftCount);
        var rightChildren = Collect(this.right, rightContainer, first, rightCount);

        // Right name-hash -> index. Duplicate keys (valid but degenerate JSON) keep the
        // last occurrence, mirroring how JSON consumers resolve duplicates.
        var rightByName = new Dictionary<ulong, int>(rightChildren.Count);
        for (int j = 0; j < rightChildren.Count; j++)
            rightByName[JsonUnescape.DecodedHash(this.right.Text.NameBytes(rightChildren[j]))] = j;

        var matchOfLeft = new int[leftChildren.Count];
        var matchedRight = new bool[rightChildren.Count];
        for (int a = 0; a < leftChildren.Count; a++)
        {
            matchOfLeft[a] = -1;
            var leftName = this.left.Text.NameBytes(leftChildren[a]);
            if (rightByName.TryGetValue(JsonUnescape.DecodedHash(leftName), out int j) && !matchedRight[j]
                && JsonUnescape.DecodedEquals(leftName, this.right.Text.NameBytes(rightChildren[j])))
            {
                matchOfLeft[a] = j;
                matchedRight[j] = true;
            }
        }

        int nextRight = 0;
        for (int a = 0; a < leftChildren.Count; a++)
        {
            int j = matchOfLeft[a];
            if (j < 0)
            {
                EmitRemoved(level, leftChildren[a], first + a);
                continue;
            }

            // Right-only keys sitting (in right order) before this match surface here, at
            // their relative position; keys whose surroundings were reordered away fall
            // through to the tail loop.
            for (int r = nextRight; r < j; r++)
            {
                if (!matchedRight[r])
                    EmitAdded(level, rightChildren[r], first + r);
            }

            nextRight = Math.Max(nextRight, j + 1);
            DiffPair(level, leftChildren[a], rightChildren[j], first + a, first + j, movedWithin: false);
        }

        for (int r = nextRight; r < rightChildren.Count; r++)
        {
            if (!matchedRight[r])
                EmitAdded(level, rightChildren[r], first + r);
        }
    }

    // ── Arrays: an over-cap middle, in place ───────────────────────────────────────────

    /// <summary>The next elements of one side of an in-place comparison, with their hashes: a
    /// ring of at most <see cref="ResyncWindow"/>, refilled from the side's children as it
    /// drains.</summary>
    private sealed class Lookahead(JsonDiffDocument document, IEnumerator<TreeNode> children, long total)
    {
        private readonly TreeNode[] nodes = new TreeNode[ResyncWindow];
        private readonly ulong[] hashes = new ulong[ResyncWindow];
        private int head;
        private long pulled;

        public int Count { get; private set; }

        /// <summary>Elements taken from the front so far.</summary>
        public long Taken { get; private set; }

        public long Remaining => total - Taken;

        public TreeNode this[int index] => nodes[(head + index) % ResyncWindow];

        public ulong HashAt(int index) => hashes[(head + index) % ResyncWindow];

        public void Fill()
        {
            while (Count < ResyncWindow && pulled < total && children.MoveNext())
            {
                int at = (head + Count) % ResyncWindow;
                nodes[at] = children.Current;
                hashes[at] = document.Hash(children.Current);
                Count++;
                pulled++;
            }
        }

        /// <summary>Where <paramref name="hash"/> first occurs from index 1 on, or -1.</summary>
        public int IndexOf(ulong hash)
        {
            for (int i = 1; i < Count; i++)
            {
                if (HashAt(i) == hash)
                    return i;
            }

            return -1;
        }

        public TreeNode Take()
        {
            var node = nodes[head];
            head = (head + 1) % ResyncWindow;
            Count--;
            Taken++;
            return node;
        }
    }

    /// <summary>How far ahead an in-place comparison looks, on each side, for the element it
    /// could not pair - so a few elements inserted or removed here and there resynchronise rather
    /// than turning every element after them into a modification.</summary>
    private const int ResyncWindow = 256;

    /// <summary>
    /// Compares an array middle too long to align, streaming both sides in step: equal pairs join
    /// runs. Where a pair differs, the next <see cref="ResyncWindow"/> elements of each side are
    /// searched for the other's element - found on the right, the right elements before it were
    /// added; found on the left, the left ones before it were removed; found on neither, the pair
    /// is descended as a modification. Once the level has emitted
    /// <see cref="MaxPositionalRecords"/> records, whatever is left of the middle becomes one range
    /// record.
    /// </summary>
    private void DiffArrayInPlace(Level level, TreeNode leftContainer, TreeNode rightContainer, long first, long leftCount, long rightCount)
    {
        long budgetEnd = (long)this.items.Count + MaxPositionalRecords;

        using var leftChildren = this.left.ChildrenFrom(leftContainer, first).GetEnumerator();
        using var rightChildren = this.right.ChildrenFrom(rightContainer, first).GetEnumerator();
        var leftAhead = new Lookahead(this.left, leftChildren, leftCount);
        var rightAhead = new Lookahead(this.right, rightChildren, rightCount);

        while (true)
        {
            leftAhead.Fill();
            rightAhead.Fill();
            if (leftAhead.Count == 0 && rightAhead.Count == 0)
                return;

            if (this.items.Count >= budgetEnd)
            {
                EmitRange(level, leftContainer, rightContainer,
                    leftAhead.Count > 0 ? leftAhead[0] : null, first + leftAhead.Taken, leftAhead.Remaining,
                    rightAhead.Count > 0 ? rightAhead[0] : null, first + rightAhead.Taken, rightAhead.Remaining);
                return;
            }

            if (leftAhead.Count > 0 && rightAhead.Count > 0)
            {
                ulong leftHash = leftAhead.HashAt(0);
                ulong rightHash = rightAhead.HashAt(0);
                if (leftHash != rightHash)
                {
                    int inserted = rightAhead.IndexOf(leftHash);
                    int removed = leftAhead.IndexOf(rightHash);
                    if (inserted > 0 && (removed < 0 || inserted <= removed))
                    {
                        for (; inserted > 0; inserted--)
                            EmitAdded(level, rightAhead.Take(), first + rightAhead.Taken - 1);
                        continue;
                    }

                    if (removed > 0)
                    {
                        for (; removed > 0; removed--)
                            EmitRemoved(level, leftAhead.Take(), first + leftAhead.Taken - 1);
                        continue;
                    }
                }

                var leftNode = leftAhead.Take();
                var rightNode = rightAhead.Take();
                DiffPair(level, leftNode, rightNode, first + leftAhead.Taken - 1, first + rightAhead.Taken - 1, movedWithin: false);
            }
            else if (leftAhead.Count > 0)
            {
                EmitRemoved(level, leftAhead.Take(), first + leftAhead.Taken - 1);
            }
            else
            {
                EmitAdded(level, rightAhead.Take(), first + rightAhead.Taken - 1);
            }

            Tick(level.LeftPosition);
        }
    }

    /// <summary>The rest of an over-cap middle, from each side's first remaining element on:
    /// shown whole, not compared.</summary>
    private void EmitRange(Level level, TreeNode leftContainer, TreeNode rightContainer,
        TreeNode? leftFirst, long leftOrdinal, long leftCount, TreeNode? rightFirst, long rightOrdinal, long rightCount)
    {
        FlushRun(level);
        long leftEnd = leftFirst is null ? -1 : this.left.End(LastOf(this.left, leftContainer, leftOrdinal + leftCount - 1));
        long rightEnd = rightFirst is null ? -1 : this.right.End(LastOf(this.right, rightContainer, rightOrdinal + rightCount - 1));
        Emit(level,
            leftFirst is { } l ? JsonDiffNode.Of(l) : JsonDiffNode.Absent,
            rightFirst is { } r ? JsonDiffNode.Of(r) : JsonDiffNode.Absent,
            DiffStatus.Modified,
            leftFirst is null ? -1 : leftOrdinal, rightFirst is null ? -1 : rightOrdinal,
            leftFirst is null ? 0 : leftCount, rightFirst is null ? 0 : rightCount,
            leftEnd, rightEnd,
            level.AnchorAt(leftFirst?.RowStart ?? level.LeftPosition),
            FlagRange);
        if (leftEnd >= 0)
            level.LeftPosition = leftEnd;

        static TreeNode LastOf(JsonDiffDocument document, TreeNode container, long ordinal)
        {
            foreach (var child in document.ChildrenFrom(container, ordinal))
                return child;
            throw new InvalidOperationException($"Child {ordinal} not found.");
        }
    }

    // ── Arrays: an in-cap middle (identity keys, or histogram anchors + Myers in the gaps) ──

    private enum ElementKind : byte
    {
        Unassigned,
        Match,     // equal hash, in order - Unchanged, no descent
        Pair,      // aligned but different - recurse
        MovedIn,   // equal pair outside the stable order - in-array move
        MovedPair, // different pair outside the stable order - recurse, flagged moved
        Insert
    }

    private readonly record struct IndexPair(int Left, int Right);

    private struct HashStats
    {
        public int Count;
        public int Ordinal;
    }

    /// <summary>A small growable buffer whose backing storage comes from ArrayPool. Array
    /// alignment creates several O(children) work lists; returning them after each level keeps
    /// repeated/nested diffs from feeding large temporary arrays to Gen2/LOH.</summary>
    private sealed class PooledBuffer<T> : IDisposable where T : struct
    {
        private T[] buffer;

        public PooledBuffer(int initialCapacity = 16)
        {
            this.buffer = ArrayPool<T>.Shared.Rent(Math.Max(1, initialCapacity));
        }

        public int Count { get; private set; }

        public T this[int index]
        {
            get => this.buffer[index];
            set => this.buffer[index] = value;
        }

        public Span<T> Span => this.buffer.AsSpan(0, this.Count);

        public void Add(T item)
        {
            if (this.Count == this.buffer.Length)
            {
                var grown = ArrayPool<T>.Shared.Rent(this.buffer.Length * 2);
                this.buffer.AsSpan(0, this.Count).CopyTo(grown);
                ArrayPool<T>.Shared.Return(this.buffer);
                this.buffer = grown;
            }

            this.buffer[this.Count++] = item;
        }

        public void Dispose()
        {
            var returned = this.buffer;
            this.buffer = Array.Empty<T>();
            this.Count = 0;
            if (returned.Length > 0)
                ArrayPool<T>.Shared.Return(returned);
        }
    }

    /// <summary>The minimum state that must survive into recursive record emission. Hash
    /// histograms, hash arrays, anchor/LIS state and Myers traces are all released by
    /// BuildArrayAlignment before this plan is returned, so nested changed arrays do not retain
    /// every ancestor level's full scratch working set.</summary>
    private sealed class ArrayAlignmentPlan : IDisposable
    {
        public ArrayAlignmentPlan(PooledBuffer<TreeNode> leftChildren, PooledBuffer<TreeNode> rightChildren,
            ElementKind[] rightKind, int[] rightPartner, bool[] leftConsumed)
        {
            this.LeftChildren = leftChildren;
            this.RightChildren = rightChildren;
            this.RightKind = rightKind;
            this.RightPartner = rightPartner;
            this.LeftConsumed = leftConsumed;
        }

        public PooledBuffer<TreeNode> LeftChildren { get; }
        public PooledBuffer<TreeNode> RightChildren { get; }
        public ElementKind[] RightKind { get; }
        public int[] RightPartner { get; }
        public bool[] LeftConsumed { get; }

        public void Dispose()
        {
            this.LeftChildren.Dispose();
            this.RightChildren.Dispose();
            ArrayPool<ElementKind>.Shared.Return(this.RightKind);
            ArrayPool<int>.Shared.Return(this.RightPartner);
            ArrayPool<bool>.Shared.Return(this.LeftConsumed);
        }
    }

    /// <summary>
    /// Aligns and emits the middle of one array level: children [first, first + count) on each
    /// side, at most <see cref="MaxAlignableArrayElements"/> each.
    /// </summary>
    private void DiffArrayMiddle(Level level, TreeNode leftContainer, TreeNode rightContainer, long first, int leftCount, int rightCount)
    {
        using var plan = BuildArrayAlignment(leftContainer, rightContainer, first, leftCount, rightCount);

        var leftChildren = plan.LeftChildren;
        var rightChildren = plan.RightChildren;
        var rightKind = plan.RightKind;
        var rightPartner = plan.RightPartner;
        var leftConsumed = plan.LeftConsumed;

        // Emission in merged order: walk the right side, interleaving Removed rows at the
        // positions the aligned pairs pin down.
        int leftPointer = 0;

        void FlushRemovedBefore(int leftOrdinalExclusive)
        {
            while (leftPointer < leftOrdinalExclusive)
            {
                if (!leftConsumed[leftPointer])
                    EmitRemoved(level, leftChildren[leftPointer], first + leftPointer);
                leftPointer++;
            }
        }

        for (int j = 0; j < rightChildren.Count; j++)
        {
            int partner = rightPartner[j];
            switch (rightKind[j])
            {
                case ElementKind.Match:
                case ElementKind.Pair:
                    FlushRemovedBefore(partner);
                    leftPointer = Math.Max(leftPointer, partner + 1);
                    DiffPair(level, leftChildren[partner], rightChildren[j], first + partner, first + j, movedWithin: false);
                    break;

                case ElementKind.MovedIn:
                    // Rendered at its new position only, badged with the source ordinal; the
                    // left pointer is NOT advanced - the element's old position contributes no row.
                    EmitMovedIn(level, leftChildren[partner], rightChildren[j], first + partner, first + j);
                    break;

                case ElementKind.MovedPair:
                    DiffPair(level, leftChildren[partner], rightChildren[j], first + partner, first + j, movedWithin: true);
                    break;

                default:
                    EmitAdded(level, rightChildren[j], first + j);
                    break;
            }
        }

        FlushRemovedBefore(leftChildren.Count);
    }

    /// <summary>Builds one array middle's alignment plan. Only the compact emission plan is
    /// returned; all other large scratch buffers are pooled/returned before recursive emission.</summary>
    private ArrayAlignmentPlan BuildArrayAlignment(TreeNode leftContainer, TreeNode rightContainer, long first, int leftCount, int rightCount)
    {
        var leftChildren = CollectPooled(this.left, leftContainer, first, leftCount);
        var rightChildren = CollectPooled(this.right, rightContainer, first, rightCount);

        ElementKind[]? rightKind = null;
        int[]? rightPartner = null;
        bool[]? leftConsumed = null;
        ulong[]? leftHashes = null;
        ulong[]? rightHashes = null;
        bool transferred = false;

        try
        {
            leftCount = leftChildren.Count;
            rightCount = rightChildren.Count;

            leftHashes = ArrayPool<ulong>.Shared.Rent(Math.Max(1, leftCount));
            rightHashes = ArrayPool<ulong>.Shared.Rent(Math.Max(1, rightCount));
            for (int i = 0; i < leftCount; i++)
                leftHashes[i] = this.left.Hash(leftChildren[i]);
            for (int j = 0; j < rightCount; j++)
                rightHashes[j] = this.right.Hash(rightChildren[j]);

            rightKind = ArrayPool<ElementKind>.Shared.Rent(Math.Max(1, rightCount));
            rightPartner = ArrayPool<int>.Shared.Rent(Math.Max(1, rightCount));
            leftConsumed = ArrayPool<bool>.Shared.Rent(Math.Max(1, leftCount));
            rightKind.AsSpan(0, rightCount).Clear();
            leftConsumed.AsSpan(0, leftCount).Clear();

            if (!TryPairByIdentity(leftChildren, rightChildren, leftHashes, rightHashes, rightKind, rightPartner, leftConsumed))
                AlignByAnchors(leftCount, rightCount, leftHashes, rightHashes, rightKind, rightPartner, leftConsumed);

            var result = new ArrayAlignmentPlan(leftChildren, rightChildren, rightKind, rightPartner, leftConsumed);
            transferred = true;
            return result;
        }
        finally
        {
            if (leftHashes is not null)
                ArrayPool<ulong>.Shared.Return(leftHashes);
            if (rightHashes is not null)
                ArrayPool<ulong>.Shared.Return(rightHashes);

            if (!transferred)
            {
                leftChildren.Dispose();
                rightChildren.Dispose();
                if (rightKind is not null)
                    ArrayPool<ElementKind>.Shared.Return(rightKind);
                if (rightPartner is not null)
                    ArrayPool<int>.Shared.Return(rightPartner);
                if (leftConsumed is not null)
                    ArrayPool<bool>.Shared.Return(leftConsumed);
            }
        }
    }

    private static PooledBuffer<TreeNode> CollectPooled(JsonDiffDocument document, TreeNode container, long first, int count)
    {
        var children = new PooledBuffer<TreeNode>(Math.Min(256, Math.Max(1, count)));
        foreach (var child in document.ChildrenFrom(container, first))
        {
            if (children.Count == count)
                break;
            children.Add(child);
        }

        return children;
    }

    /// <summary>Unique-hash anchors, their longest increasing run as the stable order (the rest
    /// are moves), and Myers in the gaps between stable anchors.</summary>
    private static void AlignByAnchors(int leftCount, int rightCount, ulong[] leftHashes, ulong[] rightHashes,
        ElementKind[] rightKind, int[] rightPartner, bool[] leftConsumed)
    {
        // One dictionary per side carries both occurrence count and the ordinal (consulted only
        // when Count == 1).
        var leftStats = BuildHashStats(leftHashes, leftCount);
        var rightStats = BuildHashStats(rightHashes, rightCount);

        using var uniquePairs = new PooledBuffer<IndexPair>(Math.Min(leftCount, rightCount));
        for (int i = 0; i < leftCount; i++)
        {
            ulong hash = leftHashes[i];
            if (leftStats[hash].Count == 1 && rightStats.TryGetValue(hash, out var right) && right.Count == 1)
                uniquePairs.Add(new IndexPair(i, right.Ordinal));
        }

        var isStable = ArrayPool<bool>.Shared.Rent(Math.Max(1, uniquePairs.Count));
        try
        {
            isStable.AsSpan(0, uniquePairs.Count).Clear();
            MarkLongestIncreasingByRight(uniquePairs, isStable);

            for (int p = 0; p < uniquePairs.Count; p++)
            {
                var pair = uniquePairs[p];
                rightKind[pair.Right] = isStable[p] ? ElementKind.Match : ElementKind.MovedIn;
                rightPartner[pair.Right] = pair.Left;
                leftConsumed[pair.Left] = true;
            }

            // Between consecutive stable anchors, align the leftover (non-unique /
            // non-moved) runs with Myers, then positionally pair the remaining edits.
            int gapLeftStart = 0, gapRightStart = 0;
            for (int p = 0; p < uniquePairs.Count; p++)
            {
                if (!isStable[p])
                    continue;

                var anchor = uniquePairs[p];
                // Adjacent anchors have no gap. On a mostly-unchanged array that is nearly
                // every pair, so skipping them avoids a pooled-buffer wrapper per element.
                if (gapLeftStart < anchor.Left || gapRightStart < anchor.Right)
                {
                    AlignGap(gapLeftStart, anchor.Left, gapRightStart, anchor.Right,
                        leftHashes, rightHashes, leftConsumed, rightKind, rightPartner);
                }
                gapLeftStart = anchor.Left + 1;
                gapRightStart = anchor.Right + 1;
            }

            if (gapLeftStart < leftCount || gapRightStart < rightCount)
            {
                AlignGap(gapLeftStart, leftCount, gapRightStart, rightCount,
                    leftHashes, rightHashes, leftConsumed, rightKind, rightPartner);
            }
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(isStable);
        }
    }

    // ── Identity keys ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pairs an array middle's elements by an identity key member, when every element on both
    /// sides is an object that has one: a scalar member with an identifier-like name (see
    /// <see cref="IsIdentityName"/>), present in every element, unique within each side, and
    /// shared by at least one pair. The first such candidate of the first left element's members
    /// is the key. The pairs' longest increasing run of right ordinals is the stable order;
    /// equal pairs outside it are moves, different ones descended pairs flagged as moved.
    /// Unpaired elements stay unassigned - added or removed. False, with nothing assigned, when
    /// there is no key.
    /// </summary>
    private bool TryPairByIdentity(PooledBuffer<TreeNode> leftChildren, PooledBuffer<TreeNode> rightChildren,
        ulong[] leftHashes, ulong[] rightHashes, ElementKind[] rightKind, int[] rightPartner, bool[] leftConsumed)
    {
        int leftCount = leftChildren.Count;
        int rightCount = rightChildren.Count;
        if (leftCount == 0 || rightCount == 0 || leftCount + rightCount < 3)
            return false;

        for (int i = 0; i < leftCount; i++)
        {
            if (leftChildren[i].FormatKind != (byte)JsonTokenKind.StartObject)
                return false;
        }

        for (int j = 0; j < rightCount; j++)
        {
            if (rightChildren[j].FormatKind != (byte)JsonTokenKind.StartObject)
                return false;
        }

        foreach (var candidate in IdentityCandidates(leftChildren[0]))
        {
            var leftKeys = ArrayPool<ulong>.Shared.Rent(leftCount);
            var rightKeys = ArrayPool<ulong>.Shared.Rent(rightCount);
            try
            {
                if (!KeyHashes(this.left, leftChildren, candidate, leftKeys) || !KeyHashes(this.right, rightChildren, candidate, rightKeys))
                    continue;

                var rightByKey = new Dictionary<ulong, int>(rightCount);
                for (int j = 0; j < rightCount; j++)
                    rightByKey[rightKeys[j]] = j;

                using var pairs = new PooledBuffer<IndexPair>(Math.Min(leftCount, rightCount));
                for (int i = 0; i < leftCount; i++)
                {
                    if (rightByKey.TryGetValue(leftKeys[i], out int j))
                        pairs.Add(new IndexPair(i, j));
                }

                if (pairs.Count == 0)
                    continue;

                var isStable = ArrayPool<bool>.Shared.Rent(pairs.Count);
                try
                {
                    isStable.AsSpan(0, pairs.Count).Clear();
                    MarkLongestIncreasingByRight(pairs, isStable);
                    for (int p = 0; p < pairs.Count; p++)
                    {
                        var (i, j) = pairs[p];
                        bool equal = leftHashes[i] == rightHashes[j];
                        rightKind[j] = isStable[p]
                            ? equal ? ElementKind.Match : ElementKind.Pair
                            : equal ? ElementKind.MovedIn : ElementKind.MovedPair;
                        rightPartner[j] = i;
                        leftConsumed[i] = true;
                    }
                }
                finally
                {
                    ArrayPool<bool>.Shared.Return(isStable);
                }

                return true;
            }
            finally
            {
                ArrayPool<ulong>.Shared.Return(leftKeys);
                ArrayPool<ulong>.Shared.Return(rightKeys);
            }
        }

        return false;
    }

    /// <summary>The raw names of the first element's scalar members that look like identifiers, in
    /// member order, at most <see cref="MaxIdentityCandidates"/>.</summary>
    private List<byte[]> IdentityCandidates(TreeNode firstElement)
    {
        var candidates = new List<byte[]>();
        foreach (var member in this.left.Children(firstElement))
        {
            if (!IsKeyValue(member))
                continue;

            var name = this.left.Text.NameBytes(member);
            if (IsIdentityName(name))
            {
                candidates.Add(name.ToArray());
                if (candidates.Count == MaxIdentityCandidates)
                    break;
            }
        }

        return candidates;
    }

    /// <summary>Each element's key value hash into <paramref name="keys"/>; false when an element
    /// lacks the member, its value is not a string or number, or two elements share a value.</summary>
    private static bool KeyHashes(JsonDiffDocument document, PooledBuffer<TreeNode> elements, byte[] name, ulong[] keys)
    {
        var seen = new HashSet<ulong>(elements.Count);
        for (int i = 0; i < elements.Count; i++)
        {
            bool found = false;
            foreach (var member in document.Children(elements[i]))
            {
                if (!JsonUnescape.DecodedEquals(document.Text.NameBytes(member), name))
                    continue;

                if (!IsKeyValue(member))
                    return false;

                keys[i] = document.Hash(member);
                found = true;
                break;
            }

            if (!found || !seen.Add(keys[i]))
                return false;
        }

        return true;
    }

    private static bool IsKeyValue(TreeNode member)
        => member.FormatKind is (byte)JsonTokenKind.String or (byte)JsonTokenKind.Number;

    /// <summary>Whether a raw member name looks like an identifier: <c>id</c>, <c>_id</c>,
    /// <c>uuid</c>, <c>guid</c>, <c>key</c> or <c>_key</c> in any case, or a name ending in
    /// <c>Id</c>, <c>ID</c>, <c>_id</c> or <c>-id</c>.</summary>
    internal static bool IsIdentityName(ReadOnlySpan<byte> name)
    {
        if (Ascii.EqualsIgnoreCase(name, "id"u8) || Ascii.EqualsIgnoreCase(name, "_id"u8)
            || Ascii.EqualsIgnoreCase(name, "uuid"u8) || Ascii.EqualsIgnoreCase(name, "guid"u8)
            || Ascii.EqualsIgnoreCase(name, "key"u8) || Ascii.EqualsIgnoreCase(name, "_key"u8))
        {
            return true;
        }

        return name.Length > 2
            && (name.EndsWith("Id"u8) || name.EndsWith("ID"u8) || name.EndsWith("_id"u8) || name.EndsWith("-id"u8));
    }

    private static Dictionary<ulong, HashStats> BuildHashStats(ulong[] hashes, int count)
    {
        var stats = new Dictionary<ulong, HashStats>(count);
        for (int i = 0; i < count; i++)
        {
            ref var value = ref CollectionsMarshal.GetValueRefOrAddDefault(stats, hashes[i], out bool exists);
            if (exists)
            {
                value.Count++;
                value.Ordinal = i;
            }
            else
            {
                value = new HashStats { Count = 1, Ordinal = i };
            }
        }

        return stats;
    }

    /// <summary>Marks the longest increasing (by right ordinal) subsequence of the pair
    /// list, which is already sorted by left ordinal. Patience algorithm, O(n log n).</summary>
    private static void MarkLongestIncreasingByRight(PooledBuffer<IndexPair> pairs, bool[] isStable)
    {
        if (pairs.Count == 0)
            return;

        var tails = ArrayPool<int>.Shared.Rent(pairs.Count);
        var predecessor = ArrayPool<int>.Shared.Rent(pairs.Count);
        int tailsCount = 0;

        try
        {
            for (int p = 0; p < pairs.Count; p++)
            {
                int right = pairs[p].Right;
                int lo = 0, hi = tailsCount;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (pairs[tails[mid]].Right < right)
                        lo = mid + 1;
                    else
                        hi = mid;
                }

                predecessor[p] = lo > 0 ? tails[lo - 1] : -1;
                if (lo == tailsCount)
                    tails[tailsCount++] = p;
                else
                    tails[lo] = p;
            }

            for (int p = tails[tailsCount - 1]; p >= 0; p = predecessor[p])
                isStable[p] = true;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(tails);
            ArrayPool<int>.Shared.Return(predecessor);
        }
    }

    /// <summary>
    /// Aligns the leftover elements of one inter-anchor gap: left ordinals
    /// [leftStart, leftEnd) and right ordinals [rightStart, rightEnd), skipping elements
    /// already consumed by unique-pairing. Equal hashes become Match, edit-script runs pair
    /// positionally into Pair (recursed later), the remainder stays Insert/unconsumed.
    /// </summary>
    private static void AlignGap(int leftStart, int leftEnd, int rightStart, int rightEnd,
        ulong[] leftHashes, ulong[] rightHashes, bool[] leftConsumed, ElementKind[] rightKind, int[] rightPartner)
    {
        // Materialize the gap's live ordinals (usually tiny - anchors carry the bulk).
        using var gapLeft = new PooledBuffer<int>();
        for (int i = leftStart; i < leftEnd; i++)
        {
            if (!leftConsumed[i])
                gapLeft.Add(i);
        }

        using var gapRight = new PooledBuffer<int>();
        for (int j = rightStart; j < rightEnd; j++)
        {
            if (rightKind[j] == ElementKind.Unassigned)
                gapRight.Add(j);
        }

        if (gapLeft.Count == 0 || gapRight.Count == 0)
            return;

        using var matches = new PooledBuffer<IndexPair>();
        if (!TryMyersDiff(gapLeft, gapRight, leftHashes, rightHashes, matches))
        {
            // Edit distance beyond the cap: positional pairing - still recursed, so the
            // common "every element tweaked" case renders as per-element Modified.
            int pairs = Math.Min(gapLeft.Count, gapRight.Count);
            for (int k = 0; k < pairs; k++)
            {
                rightKind[gapRight[k]] = ElementKind.Pair;
                rightPartner[gapRight[k]] = gapLeft[k];
                leftConsumed[gapLeft[k]] = true;
            }

            return;
        }

        // The script is a sequence of (matchedLeft, matchedRight) plus implicit runs of
        // deletions/insertions between them; pair those runs positionally.
        int prevLeft = 0, prevRight = 0;

        void PairRuns(int leftUpTo, int rightUpTo)
        {
            int deletes = leftUpTo - prevLeft;
            int inserts = rightUpTo - prevRight;
            int pairs = Math.Min(deletes, inserts);
            for (int k = 0; k < pairs; k++)
            {
                rightKind[gapRight[prevRight + k]] = ElementKind.Pair;
                rightPartner[gapRight[prevRight + k]] = gapLeft[prevLeft + k];
                leftConsumed[gapLeft[prevLeft + k]] = true;
            }
        }

        for (int m = 0; m < matches.Count; m++)
        {
            var (li, rj) = matches[m];
            PairRuns(li, rj);
            rightKind[gapRight[rj]] = ElementKind.Match;
            rightPartner[gapRight[rj]] = gapLeft[li];
            leftConsumed[gapLeft[li]] = true;
            prevLeft = li + 1;
            prevRight = rj + 1;
        }

        PairRuns(gapLeft.Count, gapRight.Count);
    }

    /// <summary>
    /// Greedy Myers over the two gap hash sequences, appending matched index pairs (into
    /// the gap lists) in order. Returns false when the edit distance exceeds
    /// <see cref="MaxMyersEditDistance"/> (caller falls back to positional pairing). The
    /// backtrack trace stores only the parity-valid diagonals for each d, cutting it from a
    /// rectangular ~526K ints to ~132K at the cap, and both trace/vector come from pools.
    /// </summary>
    private static bool TryMyersDiff(PooledBuffer<int> gapLeft, PooledBuffer<int> gapRight,
        ulong[] leftHashes, ulong[] rightHashes, PooledBuffer<IndexPair> matches)
    {
        int n = gapLeft.Count, m = gapRight.Count;
        int maxD = Math.Min(n + m, MaxMyersEditDistance);
        int offset = maxD;

        var v = ArrayPool<int>.Shared.Rent(2 * maxD + 1);
        int traceLength = (maxD + 1) * (maxD + 2) / 2;
        var trace = ArrayPool<int>.Shared.Rent(traceLength);
        v.AsSpan(0, 2 * maxD + 1).Clear();

        bool Equal(int i, int j) => leftHashes[gapLeft[i]] == rightHashes[gapRight[j]];

        int finalD = -1;
        try
        {
            for (int d = 0; d <= maxD && finalD < 0; d++)
            {
                for (int k = -d; k <= d; k += 2)
                {
                    int x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])
                        ? v[offset + k + 1]
                        : v[offset + k - 1] + 1;
                    int y = x - k;
                    while (x < n && y < m && Equal(x, y))
                    {
                        x++;
                        y++;
                    }

                    v[offset + k] = x;
                    if (x >= n && y >= m)
                    {
                        finalD = d;
                        break;
                    }
                }

                int traceStart = d * (d + 1) / 2;
                for (int ordinal = 0, k = -d; ordinal <= d; ordinal++, k += 2)
                    trace[traceStart + ordinal] = v[offset + k];
            }

            if (finalD < 0)
                return false;

            // Backtrack, collecting diagonal (match) runs in reverse order.
            int cx = n, cy = m;
            for (int d = finalD; d > 0; d--)
            {
                int k = cx - cy;
                int previousD = d - 1;
                int previousStart = previousD * (previousD + 1) / 2;

                int leftK = k - 1;
                int rightK = k + 1;
                int leftX = leftK >= -previousD && leftK <= previousD
                    ? trace[previousStart + (leftK + previousD) / 2]
                    : int.MinValue;
                int rightX = rightK >= -previousD && rightK <= previousD
                    ? trace[previousStart + (rightK + previousD) / 2]
                    : int.MinValue;

                int prevK = k == -d || (k != d && leftX < rightX) ? rightK : leftK;
                int px = trace[previousStart + (prevK + previousD) / 2];
                int py = px - prevK;

                while (cx > px && cy > py && cx > 0 && cy > 0)
                {
                    matches.Add(new IndexPair(cx - 1, cy - 1));
                    cx--;
                    cy--;
                }

                cx = px;
                cy = py;
            }

            while (cx > 0 && cy > 0)
            {
                matches.Add(new IndexPair(cx - 1, cy - 1));
                cx--;
                cy--;
            }

            matches.Span.Reverse();
            return true;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(v);
            ArrayPool<int>.Shared.Return(trace);
        }
    }

    // ── Cross-parent move reconciliation ───────────────────────────────────────────────

    /// <summary>
    /// Pairs Removed/Added container records with identical content hashes - exactly once
    /// on each side, the unique-anchor rule - and rewrites both in place to Moved, each
    /// carrying both nodes and its partner's record index. Containers only: a
    /// scalar 1/true/"" hashes identically everywhere by design, so pairing scalars would
    /// manufacture spurious moves. Runs over whole-subtree records only, so cost is bounded
    /// by the size of the change, not the document.
    /// </summary>
    private void ReconcileCrossParentMoves()
    {
        foreach (var (hash, removed) in this.removedContainersByHash)
        {
            if (removed.Count != 1)
                continue;
            if (!this.addedContainersByHash.TryGetValue(hash, out var added) || added.Count != 1)
                continue;

            LinkMove(removed.RecordIndex, added.RecordIndex, firstChild: -1, childrenEnd: -1);
        }
    }

    /// <summary>
    /// Rewrites a Removed and an Added record into the two ends of one move: partner fields
    /// first, StatusBits last (release) - paired with GetRecord reading StatusBits first
    /// (acquire). A move whose content changed also gets the destination's children, emitted
    /// after the descent.
    /// </summary>
    private void LinkMove(int removedIndex, int addedIndex, int firstChild, int childrenEnd, bool approximate = false)
    {
        ref var removedRecord = ref this.items.ItemRef(removedIndex);
        ref var addedRecord = ref this.items.ItemRef(addedIndex);

        Volatile.Write(ref removedRecord.RightRowStart, addedRecord.RightRowStart);
        Volatile.Write(ref removedRecord.RightValueStart, addedRecord.RightValueStart);
        Volatile.Write(ref removedRecord.RightOrdinal, addedRecord.RightOrdinal);
        Volatile.Write(ref removedRecord.RightEnd, addedRecord.RightEnd);
        Volatile.Write(ref removedRecord.RightDepth, addedRecord.RightDepth);
        Volatile.Write(ref removedRecord.MovePartnerRecord, addedIndex);
        Volatile.Write(ref addedRecord.LeftRowStart, removedRecord.LeftRowStart);
        Volatile.Write(ref addedRecord.LeftValueStart, removedRecord.LeftValueStart);
        Volatile.Write(ref addedRecord.LeftOrdinal, removedRecord.LeftOrdinal);
        Volatile.Write(ref addedRecord.LeftEnd, removedRecord.LeftEnd);
        Volatile.Write(ref addedRecord.LeftDepth, removedRecord.LeftDepth);
        Volatile.Write(ref addedRecord.MovePartnerRecord, removedIndex);
        Volatile.Write(ref addedRecord.ChildrenEnd, childrenEnd);
        Volatile.Write(ref addedRecord.FirstChild, firstChild);
        Volatile.Write(ref removedRecord.StatusBits, (int)DiffStatus.Moved | FlagMoveSource | FlagCrossParentMove);
        Volatile.Write(ref addedRecord.StatusBits, (int)DiffStatus.Moved | FlagCrossParentMove | (approximate ? FlagApproximate : 0));
    }

    // ── Similarity pairing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Pairs the removed and added containers exact pairing left over, when they share enough of
    /// their direct children (see <see cref="SimilarityThreshold"/>) - a block that moved and was
    /// edited on the way. Each pair becomes a move, and the destination is descended against the
    /// source, so the edit shows as the leaf that changed rather than the whole block removed and
    /// added. Pairs are mutually best matches, same kind only, one round: the descent's own
    /// removed and added records are not paired again.
    ///
    /// Bounded by the residue, which the Merkle short-circuit keeps small: skipped outright past
    /// <see cref="MaxSimilarityPairs"/> candidate pairs, and a container past
    /// <see cref="MaxSimilarityChildren"/> children is not scored.
    /// </summary>
    private void PairSimilarContainers()
    {
        var removed = new List<(int Record, TreeNode Node, Dictionary<ulong, int> Children, int Count)>();
        var added = new List<(int Record, TreeNode Node, Dictionary<ulong, int> Children, int Count)>();
        foreach (int record in this.removedContainers)
        {
            if (this.GetRecord(record).Status == DiffStatus.Removed)
                removed.Add((record, default, null!, 0));
        }

        foreach (int record in this.addedContainers)
        {
            if (this.GetRecord(record).Status == DiffStatus.Added)
                added.Add((record, default, null!, 0));
        }

        if (removed.Count == 0 || added.Count == 0 || (long)removed.Count * added.Count > MaxSimilarityPairs)
            return;

        for (int i = removed.Count - 1; i >= 0; i--)
        {
            var node = this.left.NodeAt(this.GetRecord(removed[i].Record).Left);
            if (Signature(this.left, node) is not { } signature)
                removed.RemoveAt(i);
            else
                removed[i] = (removed[i].Record, node, signature.Children, signature.Count);
        }

        for (int j = added.Count - 1; j >= 0; j--)
        {
            var node = this.right.NodeAt(this.GetRecord(added[j].Record).Right);
            if (Signature(this.right, node) is not { } signature)
                added.RemoveAt(j);
            else
                added[j] = (added[j].Record, node, signature.Children, signature.Count);
        }

        // Scores, with identical content left out: exact pairing already declined those as
        // ambiguous, and similarity must not decide what it could not.
        var scores = new double[removed.Count, added.Count];
        for (int i = 0; i < removed.Count; i++)
        {
            ulong removedHash = this.left.Hash(removed[i].Node);
            for (int j = 0; j < added.Count; j++)
            {
                if (removed[i].Node.FormatKind == added[j].Node.FormatKind && removedHash != this.right.Hash(added[j].Node))
                    scores[i, j] = Similarity(removed[i].Children, removed[i].Count, added[j].Children, added[j].Count);
            }
        }

        for (int i = 0; i < removed.Count; i++)
        {
            int j = UniqueBest(scores, i, added.Count, byRow: true);
            if (j < 0 || UniqueBest(scores, j, removed.Count, byRow: false) != i)
                continue;

            this.cancellationToken.ThrowIfCancellationRequested();
            DescendMove(removed[i].Record, removed[i].Node, added[j].Record, added[j].Node);
        }
    }

    /// <summary>The one candidate scoring best, at or above <see cref="SimilarityThreshold"/>, for
    /// row (or column) <paramref name="at"/> of the score matrix; -1 when none does or two tie -
    /// a tie is as ambiguous as two identical blocks.</summary>
    private static int UniqueBest(double[,] scores, int at, int count, bool byRow)
    {
        int best = -1;
        double bestScore = SimilarityThreshold;
        bool tied = false;
        for (int other = 0; other < count; other++)
        {
            double score = byRow ? scores[at, other] : scores[other, at];
            if (score < bestScore || (best < 0 && score < SimilarityThreshold))
                continue;

            if (best >= 0 && score == bestScore)
            {
                tied = true;
                continue;
            }

            best = other;
            bestScore = score;
            tied = false;
        }

        return tied ? -1 : best;
    }

    /// <summary>Links a similar pair as a move and descends it, the destination's children
    /// emitted at the end of the log.</summary>
    private void DescendMove(int removedIndex, TreeNode leftNode, int addedIndex, TreeNode rightNode)
    {
        var source = this.GetRecord(removedIndex);
        var destination = this.GetRecord(addedIndex);
        int firstChild = this.items.Count;

        var children = new Level(destination.Depth + 1, addedIndex, this.left.Reader.FirstChildPosition(leftNode.ValueStart),
            destination.LeftAnchor, source.LeftDepth + 1, destination.RightDepth + 1);
        bool approximate = DiffChildren(children, leftNode, rightNode);

        LinkMove(removedIndex, addedIndex, firstChild, this.items.Count, approximate);
    }

    /// <summary>A container's direct children as a multiset of signatures - an object member's
    /// name and value together, an array element's value - and how many there are; null past
    /// <see cref="MaxSimilarityChildren"/>.</summary>
    private static (Dictionary<ulong, int> Children, int Count)? Signature(JsonDiffDocument document, TreeNode container)
    {
        var children = new Dictionary<ulong, int>();
        int count = 0;
        bool isObject = container.FormatKind == (byte)JsonTokenKind.StartObject;
        foreach (var child in document.Children(container))
        {
            if (++count > MaxSimilarityChildren)
                return null;

            ulong signature = document.Hash(child);
            if (isObject)
            {
                ulong name = JsonUnescape.DecodedHash(document.Text.NameBytes(child));
                signature ^= name + 0x9E3779B97F4A7C15UL + (signature << 6) + (signature >> 2);
            }

            CollectionsMarshal.GetValueRefOrAddDefault(children, signature, out _)++;
        }

        return (children, count);
    }

    /// <summary>The Jaccard index of two multisets of child signatures; 0 when both are empty.</summary>
    private static double Similarity(Dictionary<ulong, int> left, int leftCount, Dictionary<ulong, int> right, int rightCount)
    {
        if (leftCount + rightCount == 0)
            return 0;

        var (smaller, larger) = left.Count <= right.Count ? (left, right) : (right, left);
        long shared = 0;
        foreach (var (signature, count) in smaller)
        {
            if (larger.TryGetValue(signature, out int other))
                shared += Math.Min(count, other);
        }

        return (double)shared / (leftCount + rightCount - shared);
    }
}
