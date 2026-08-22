using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Infrastructure;

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
/// <see cref="JsonDiffIndex.PackedDiffRecord"/>.
/// </summary>
/// <param name="Index">This record's index in the diff log.</param>
/// <param name="LeftToken">Token index in the left document, or -1 when absent there.</param>
/// <param name="RightToken">Token index in the right document, or -1 when absent there.</param>
/// <param name="Status">How this node differs.</param>
/// <param name="Depth">Merged-tree nesting depth (0 at the root).</param>
/// <param name="ParentRecord">Record index of the enclosing container's record, or -1 at the root.</param>
/// <param name="SubtreeEnd">Exclusive end of this record's descendant records; <c>Index + 1</c>
/// for a record without descendants, -1 while the descent below it is still streaming.</param>
/// <param name="IsMoveSource">For a cross-parent <see cref="DiffStatus.Moved"/> pair: true on the
/// record at the old position (the stub), false at the new one.</param>
/// <param name="MovePartnerRecord">For a cross-parent move: the record index of the other end;
/// -1 otherwise.</param>
/// <param name="LeftArrayIndex">Ordinal among the left parent array's children, or -1 (object
/// member, root, or absent side). For an in-array <see cref="DiffStatus.Moved"/> this is the
/// element's source position - what the "moved from [n]" badge shows.</param>
/// <param name="RightArrayIndex">Ordinal among the right parent array's children, or -1.</param>
/// <param name="IsAlignmentApproximate">True on an array container whose element count exceeded
/// <see cref="JsonDiffIndex.MaxAlignableArrayElements"/>, so it was not descended into.</param>
public readonly record struct JsonDiffRecord(
    int Index,
    int LeftToken,
    int RightToken,
    DiffStatus Status,
    int Depth,
    int ParentRecord,
    int SubtreeEnd,
    bool IsMoveSource,
    int MovePartnerRecord,
    int LeftArrayIndex,
    int RightArrayIndex,
    bool IsAlignmentApproximate);

/// <summary>
/// The headless semantic differ (diff plan stages 2-3): compares two fully indexed JSON
/// documents by Merkle content hash (see <see cref="JsonIndexOptions.ComputeContentHashes"/>)
/// and publishes fixed-size records in merged render order - the record log IS the flattened
/// diff tree, walked directly by the diff row collection. Same publishing shape as the other
/// scanners (<see cref="AppendLogIndexBase{T}"/>), so it gets IsComplete/Failure/waiters and
/// lock-free reads for free.
///
/// Key properties, each load-bearing:
///
///  - Equal hashes never descend: a subtree that didn't change costs one record (or zero,
///    inside an undescended region), which is what makes multi-GB diffs viable.
///  - Children match by identity (object: decoded name; array: anchored hash), never by
///    token position, so index shifts cannot produce spurious differences.
///  - Added/Removed subtrees are emitted whole (one record, no descent); the cross-parent
///    move pass over those records is therefore bounded by the size of the change.
///  - Two fields mutate after publication (the move pass rewrites Status/partner fields;
///    a container's SubtreeEnd finalizes after its descent): both use the same
///    publish-then-mutate Volatile discipline as PackedToken.EndIndex, with StatusBits as
///    the release/acquire gate (written last, read first).
///
/// The diff runs on its own dedicated thread with an oversized stack: the descent recurses
/// per nesting level and the token index permits depths up to 4095, which could overflow a
/// default 1MB task stack.
/// </summary>
public sealed class JsonDiffIndex : AppendLogIndexBase<JsonDiffIndex.PackedDiffRecord>
{
    /// <summary>Arrays with more direct elements than this on either side are not descended
    /// into - the container is flagged <see cref="JsonDiffRecord.IsAlignmentApproximate"/>
    /// instead. Part of the design, not a limitation to discover later: alignment needs both
    /// child-hash sequences in memory, and per-element records for a 10M-element array would
    /// blow the memory budget the rest of the app fights for.</summary>
    public const int MaxAlignableArrayElements = 100_000;

    // Myers inside inter-anchor gaps gives up past this many edit steps and falls back to
    // positional pairing - keeps a pathological gap O(gap * MaxMyersEditDistance) instead
    // of quadratic.
    private const int MaxMyersEditDistance = 512;

    private const int StatusMask = 0x7;
    private const int FlagMoveSource = 1 << 3;
    private const int FlagApproximate = 1 << 4;
    private const int FlagCrossParentMove = 1 << 5;

    /// <summary>
    /// Compact stored form of one <see cref="JsonDiffRecord"/>. StatusBits carries the
    /// <see cref="DiffStatus"/> in its low bits plus the flag bits above. StatusBits,
    /// LeftToken, RightToken, MovePartnerRecord and SubtreeEnd may be mutated after
    /// publication (move reconciliation / descent finalization) and are accessed with
    /// Volatile on both sides; StatusBits is always written LAST and read FIRST, so a
    /// reader that observes a mutated status also observes the partner fields that came
    /// with it. Public only because it parameterizes the base class.
    /// </summary>
    public struct PackedDiffRecord
    {
        public int LeftToken;
        public int RightToken;
        public int ParentRecord;
        public int SubtreeEnd;
        public int StatusBits;
        public int MovePartnerRecord;
        public int LeftArrayIndex;
        public int RightArrayIndex;
        public ushort Depth;
    }

    private readonly JsonStructureIndex leftIndex;
    private readonly MMapFile leftFile;
    private readonly JsonStructureIndex rightIndex;
    private readonly MMapFile rightFile;
    private readonly IProgressReporter? progressReporter;
    private readonly CancellationToken cancellationToken;

    // Removed/Added *container* records bucketed by content hash for the cross-parent move
    // pass. Populated during the descent (only whole-subtree records land here, so this is
    // bounded by the size of the change), consumed once after it.
    private readonly Dictionary<ulong, (int RecordIndex, int Count)> removedContainersByHash = new();
    private readonly Dictionary<ulong, (int RecordIndex, int Count)> addedContainersByHash = new();

    private long progressEstimate;
    private long nextProgressReport;

    public Task IndexingTask { get; private set; } = Task.CompletedTask;

    public int RecordCount => this.ItemCount;

    public Task WaitForRecordCountAsync(int targetCount) => this.WaitForCountAsync(targetCount);

    private JsonDiffIndex(JsonStructureIndex leftIndex, MMapFile leftFile, JsonStructureIndex rightIndex, MMapFile rightFile,
        IProgressReporter? progressReporter, CancellationToken cancellationToken)
    {
        this.leftIndex = leftIndex;
        this.leftFile = leftFile;
        this.rightIndex = rightIndex;
        this.rightFile = rightFile;
        this.progressReporter = progressReporter;
        this.cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Starts the diff worker. It first waits for BOTH indexes to finish (container hashes
    /// are only final once every container closes); if either side fails or is cancelled the
    /// diff completes empty - side failures are the caller's to attribute and report.
    /// The caller (JsonDiffSession) guarantees both mappings outlive <see cref="IndexingTask"/>.
    /// </summary>
    public static JsonDiffIndex Start(JsonStructureIndex leftIndex, MMapFile leftFile, JsonStructureIndex rightIndex, MMapFile rightFile,
        IProgressReporter? progressReporter = null, CancellationToken cancellationToken = default)
    {
        var diff = new JsonDiffIndex(leftIndex, leftFile, rightIndex, rightFile, progressReporter, cancellationToken);

        // A dedicated thread with an oversized stack instead of Task.Run: the descent
        // recurses per nesting level (up to the index's 4095 depth cap), which does not fit
        // a default 1MB pool-thread stack. TCS mirrors Task.Run's completion semantics.
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
            Volatile.Read(ref packed.LeftToken),
            Volatile.Read(ref packed.RightToken),
            (DiffStatus)(statusBits & StatusMask),
            packed.Depth,
            packed.ParentRecord,
            Volatile.Read(ref packed.SubtreeEnd),
            (statusBits & FlagMoveSource) != 0,
            Volatile.Read(ref packed.MovePartnerRecord),
            packed.LeftArrayIndex,
            packed.RightArrayIndex,
            (statusBits & FlagApproximate) != 0);
    }

    // ── The worker ─────────────────────────────────────────────────────────────────────

    private void Run()
    {
        // Both sides must be fully indexed before container hashes are final. A faulted or
        // cancelled side means there is nothing to diff - complete empty; the session/view
        // model attributes the side failure.
        try
        {
            Task.WaitAll(new[] { this.leftIndex.IndexingTask, this.rightIndex.IndexingTask }, this.cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return;
        }

        if (!this.leftIndex.HasContentHashes || !this.rightIndex.HasContentHashes)
            throw new InvalidOperationException("Diffing requires both indexes to be built with JsonIndexOptions.ComputeContentHashes.");

        if (this.leftIndex.TokenCount == 0 && this.rightIndex.TokenCount == 0)
            return;

        this.progressEstimate = Math.Max(1, this.leftIndex.TokenCount);
        this.nextProgressReport = 1;

        if (this.leftIndex.TokenCount == 0)
        {
            Emit(-1, 0, DiffStatus.Added, 0, -1, -1, -1);
            return;
        }

        if (this.rightIndex.TokenCount == 0)
        {
            Emit(0, -1, DiffStatus.Removed, 0, -1, -1, -1);
            return;
        }

        DiffNode(0, 0, 0, -1, -1, -1);
        ReconcileCrossParentMoves();

        this.progressReporter?.Report("Comparing", this.progressEstimate, this.progressEstimate);
    }

    private ulong LeftHash(int token) => (ulong)this.leftIndex.GetContentHash(token);
    private ulong RightHash(int token) => (ulong)this.rightIndex.GetContentHash(token);

    private static bool IsContainer(JsonTokenKind kind) => kind is JsonTokenKind.StartObject or JsonTokenKind.StartArray;

    private int Emit(int leftToken, int rightToken, DiffStatus status, int depth, int parentRecord,
        int leftArrayIndex, int rightArrayIndex, int flags = 0, int subtreeEnd = 0)
    {
        int index = this.items.Count;
        this.items.Add(new PackedDiffRecord
        {
            LeftToken = leftToken,
            RightToken = rightToken,
            ParentRecord = parentRecord,
            SubtreeEnd = subtreeEnd == 0 ? index + 1 : subtreeEnd,
            StatusBits = (int)status | flags,
            MovePartnerRecord = -1,
            LeftArrayIndex = leftArrayIndex,
            RightArrayIndex = rightArrayIndex,
            Depth = (ushort)depth
        });
        this.OnItemsPublished(index + 1);

        if ((index & 0xFFF) == 0)
        {
            this.cancellationToken.ThrowIfCancellationRequested();
            if (index >= this.nextProgressReport)
            {
                // Records-emitted against the left token count: a rough denominator, but a
                // fine one - progress only needs to visibly move.
                this.progressReporter?.Report("Comparing", Math.Min(index, this.progressEstimate - 1), this.progressEstimate);
                this.nextProgressReport = index + 4096;
            }
        }

        return index;
    }

    private void EmitRemoved(int leftToken, int depth, int parentRecord, int leftArrayIndex)
    {
        int record = Emit(leftToken, -1, DiffStatus.Removed, depth, parentRecord, leftArrayIndex, -1);

        var token = this.leftIndex.GetToken(leftToken);
        if (IsContainer(token.Kind))
            RegisterMoveCandidate(this.removedContainersByHash, LeftHash(leftToken), record);
    }

    private void EmitAdded(int rightToken, int depth, int parentRecord, int rightArrayIndex)
    {
        int record = Emit(-1, rightToken, DiffStatus.Added, depth, parentRecord, -1, rightArrayIndex);

        var token = this.rightIndex.GetToken(rightToken);
        if (IsContainer(token.Kind))
            RegisterMoveCandidate(this.addedContainersByHash, RightHash(rightToken), record);
    }

    private static void RegisterMoveCandidate(Dictionary<ulong, (int RecordIndex, int Count)> bucket, ulong hash, int record)
    {
        bucket[hash] = bucket.TryGetValue(hash, out var existing)
            ? (existing.RecordIndex, existing.Count + 1)
            : (record, 1);
    }

    /// <summary>
    /// Diffs one matched node pair. Equal hashes emit a single Unchanged record and stop -
    /// the Merkle short-circuit. Same-kind containers with differing hashes descend; every
    /// other combination is a Modified leaf record (the panes each render their own side).
    /// </summary>
    private void DiffNode(int leftToken, int rightToken, int depth, int parentRecord, int leftArrayIndex, int rightArrayIndex)
    {
        ulong leftHash = LeftHash(leftToken);
        ulong rightHash = RightHash(rightToken);

        if (leftHash == rightHash)
        {
            Emit(leftToken, rightToken, DiffStatus.Unchanged, depth, parentRecord, leftArrayIndex, rightArrayIndex);
            return;
        }

        var leftInfo = this.leftIndex.GetToken(leftToken);
        var rightInfo = this.rightIndex.GetToken(rightToken);

        if (leftInfo.Kind != rightInfo.Kind || !IsContainer(leftInfo.Kind))
        {
            Emit(leftToken, rightToken, DiffStatus.Modified, depth, parentRecord, leftArrayIndex, rightArrayIndex);
            return;
        }

        int record = Emit(leftToken, rightToken, DiffStatus.Modified, depth, parentRecord, leftArrayIndex, rightArrayIndex, subtreeEnd: -1);

        bool approximate = false;
        if (leftInfo.Kind == JsonTokenKind.StartObject)
            DiffObjectChildren(leftToken, rightToken, depth + 1, record);
        else
            approximate = !DiffArrayChildren(leftToken, rightToken, depth + 1, record);

        ref var packed = ref this.items.ItemRef(record);
        if (approximate)
            Volatile.Write(ref packed.StatusBits, packed.StatusBits | FlagApproximate);
        Volatile.Write(ref packed.SubtreeEnd, this.items.Count);
    }

    // ── Objects ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Matches one level's children by decoded property name (hash first, byte-verify on
    /// match to guard against name-hash collisions), then emits in merged key order: the
    /// left document's order is the base, right-only keys slot in at their relative
    /// position from the right. All per-level state is dropped on return - memory is
    /// O(widest changed level), never O(document).
    /// </summary>
    private void DiffObjectChildren(int leftContainer, int rightContainer, int depth, int parentRecord)
    {
        var leftChildren = CollectChildren(this.leftIndex, leftContainer);
        var rightChildren = CollectChildren(this.rightIndex, rightContainer);

        // Right name-hash -> ordinal. Duplicate keys (valid but degenerate JSON) keep the
        // last occurrence, mirroring how JSON consumers resolve duplicates.
        var rightByName = new Dictionary<ulong, int>(rightChildren.Count);
        var rightNameHashes = new ulong[rightChildren.Count];
        for (int j = 0; j < rightChildren.Count; j++)
        {
            rightNameHashes[j] = NameHash(this.rightIndex, this.rightFile, rightChildren[j]);
            rightByName[rightNameHashes[j]] = j;
        }

        var matchOfLeft = new int[leftChildren.Count];
        var matchedRight = new bool[rightChildren.Count];
        for (int a = 0; a < leftChildren.Count; a++)
        {
            matchOfLeft[a] = -1;
            ulong nameHash = NameHash(this.leftIndex, this.leftFile, leftChildren[a]);
            if (rightByName.TryGetValue(nameHash, out int j) && !matchedRight[j]
                && NamesEqual(leftChildren[a], rightChildren[j]))
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
                EmitRemoved(leftChildren[a], depth, parentRecord, -1);
                continue;
            }

            // Right-only keys sitting (in right order) before this match surface here, at
            // their relative position; keys whose surroundings were reordered away fall
            // through to the tail loop.
            for (int r = nextRight; r < j; r++)
            {
                if (!matchedRight[r])
                    EmitAdded(rightChildren[r], depth, parentRecord, -1);
            }

            nextRight = Math.Max(nextRight, j + 1);
            DiffNode(leftChildren[a], rightChildren[j], depth, parentRecord, -1, -1);
        }

        for (int r = nextRight; r < rightChildren.Count; r++)
        {
            if (!matchedRight[r])
                EmitAdded(rightChildren[r], depth, parentRecord, -1);
        }
    }

    private static List<int> CollectChildren(JsonStructureIndex index, int containerToken)
    {
        var children = new List<int>();
        int end = index.GetToken(containerToken).EndIndex;
        int i = containerToken + 1;
        while (i < end)
        {
            children.Add(i);
            var token = index.GetToken(i);
            i = IsContainer(token.Kind) ? token.EndIndex + 1 : i + 1;
        }

        return children;
    }

    private static ulong NameHash(JsonStructureIndex index, MMapFile file, int token)
    {
        var info = index.GetToken(token);
        return JsonUnescape.DecodedHash(file.GetSpan(info.NameOffset, info.NameLength));
    }

    private bool NamesEqual(int leftToken, int rightToken)
    {
        var left = this.leftIndex.GetToken(leftToken);
        var right = this.rightIndex.GetToken(rightToken);
        return JsonUnescape.DecodedEquals(
            this.leftFile.GetSpan(left.NameOffset, left.NameLength),
            this.rightFile.GetSpan(right.NameOffset, right.NameLength));
    }

    // ── Arrays (stage 3: histogram anchors + Myers in the gaps) ────────────────────────

    private enum ElementKind : byte
    {
        Unassigned,
        Match,     // equal hash - Unchanged, no descent
        Pair,      // aligned but different - recurse
        MovedIn,   // unique-hash pair outside the stable order - in-array move
        Insert
    }

    /// <summary>
    /// Aligns and emits one array level. Returns false when either side exceeds
    /// <see cref="MaxAlignableArrayElements"/> - the caller badges the container
    /// approximate and the level is not descended.
    /// </summary>
    private bool DiffArrayChildren(int leftContainer, int rightContainer, int depth, int parentRecord)
    {
        if (CountChildrenExceeds(this.leftIndex, leftContainer, MaxAlignableArrayElements)
            || CountChildrenExceeds(this.rightIndex, rightContainer, MaxAlignableArrayElements))
        {
            return false;
        }

        var leftChildren = CollectChildren(this.leftIndex, leftContainer);
        var rightChildren = CollectChildren(this.rightIndex, rightContainer);

        var leftHashes = new ulong[leftChildren.Count];
        for (int i = 0; i < leftChildren.Count; i++)
            leftHashes[i] = LeftHash(leftChildren[i]);
        var rightHashes = new ulong[rightChildren.Count];
        for (int j = 0; j < rightChildren.Count; j++)
            rightHashes[j] = RightHash(rightChildren[j]);

        // Anchor pass, O(N): hashes appearing exactly once on each side pair up.
        var leftCounts = CountByHash(leftHashes, out var leftOrdinalOfUnique);
        var rightCounts = CountByHash(rightHashes, out var rightOrdinalOfUnique);

        var uniquePairs = new List<(int Left, int Right)>();
        for (int i = 0; i < leftHashes.Length; i++)
        {
            ulong hash = leftHashes[i];
            if (leftCounts[hash] == 1 && rightCounts.TryGetValue(hash, out int rc) && rc == 1)
                uniquePairs.Add((i, rightOrdinalOfUnique[hash]));
        }

        // Stable anchors are the longest increasing run of unique pairs; unique pairs that
        // cross that order are genuine relocations - in-array moves.
        var isStable = LongestIncreasingByRight(uniquePairs);

        var rightKind = new ElementKind[rightChildren.Count];
        var rightPartner = new int[rightChildren.Count];
        var leftConsumed = new bool[leftChildren.Count];

        for (int p = 0; p < uniquePairs.Count; p++)
        {
            var (li, rj) = uniquePairs[p];
            rightKind[rj] = isStable[p] ? ElementKind.Match : ElementKind.MovedIn;
            rightPartner[rj] = li;
            leftConsumed[li] = true;
        }

        // Between consecutive stable anchors, align the leftover (non-unique / non-moved)
        // runs with Myers over the hash sequences, then pair leftover delete+insert runs
        // positionally so changed elements recurse instead of rendering as remove+add.
        int gapLeftStart = 0, gapRightStart = 0;
        foreach (var (anchorLeft, anchorRight) in StableAnchorsInOrder(uniquePairs, isStable))
        {
            AlignGap(gapLeftStart, anchorLeft, gapRightStart, anchorRight,
                leftHashes, rightHashes, leftConsumed, rightKind, rightPartner);
            gapLeftStart = anchorLeft + 1;
            gapRightStart = anchorRight + 1;
        }

        AlignGap(gapLeftStart, leftChildren.Count, gapRightStart, rightChildren.Count,
            leftHashes, rightHashes, leftConsumed, rightKind, rightPartner);

        // Emission in merged order: walk the right side, interleaving Removed rows at the
        // positions the aligned pairs pin down.
        int leftPointer = 0;

        void FlushRemovedBefore(int leftOrdinalExclusive)
        {
            while (leftPointer < leftOrdinalExclusive)
            {
                if (!leftConsumed[leftPointer])
                    EmitRemoved(leftChildren[leftPointer], depth, parentRecord, leftPointer);
                leftPointer++;
            }
        }

        for (int j = 0; j < rightChildren.Count; j++)
        {
            switch (rightKind[j])
            {
                case ElementKind.Match:
                    FlushRemovedBefore(rightPartner[j]);
                    leftPointer = Math.Max(leftPointer, rightPartner[j] + 1);
                    Emit(leftChildren[rightPartner[j]], rightChildren[j], DiffStatus.Unchanged, depth, parentRecord, rightPartner[j], j);
                    break;

                case ElementKind.Pair:
                    FlushRemovedBefore(rightPartner[j]);
                    leftPointer = Math.Max(leftPointer, rightPartner[j] + 1);
                    DiffNode(leftChildren[rightPartner[j]], rightChildren[j], depth, parentRecord, rightPartner[j], j);
                    break;

                case ElementKind.MovedIn:
                    // Rendered at its new position only, badged with the source ordinal
                    // (LeftArrayIndex); the left pointer is NOT advanced - the element's
                    // old position contributes no row.
                    Emit(leftChildren[rightPartner[j]], rightChildren[j], DiffStatus.Moved, depth, parentRecord, rightPartner[j], j);
                    break;

                default:
                    EmitAdded(rightChildren[j], depth, parentRecord, j);
                    break;
            }
        }

        FlushRemovedBefore(leftChildren.Count);
        return true;
    }

    private static bool CountChildrenExceeds(JsonStructureIndex index, int containerToken, int cap)
    {
        int end = index.GetToken(containerToken).EndIndex;
        int i = containerToken + 1;
        int count = 0;
        while (i < end)
        {
            if (++count > cap)
                return true;
            var token = index.GetToken(i);
            i = IsContainer(token.Kind) ? token.EndIndex + 1 : i + 1;
        }

        return false;
    }

    private static Dictionary<ulong, int> CountByHash(ulong[] hashes, out Dictionary<ulong, int> ordinalOfUnique)
    {
        var counts = new Dictionary<ulong, int>(hashes.Length);
        ordinalOfUnique = new Dictionary<ulong, int>(hashes.Length);
        for (int i = 0; i < hashes.Length; i++)
        {
            counts[hashes[i]] = counts.TryGetValue(hashes[i], out int c) ? c + 1 : 1;
            ordinalOfUnique[hashes[i]] = i; // only consulted when count == 1, so last-write is fine
        }

        return counts;
    }

    /// <summary>Marks the longest increasing (by right ordinal) subsequence of the pair
    /// list, which is already sorted by left ordinal. Patience algorithm, O(n log n).</summary>
    private static bool[] LongestIncreasingByRight(List<(int Left, int Right)> pairs)
    {
        var isStable = new bool[pairs.Count];
        if (pairs.Count == 0)
            return isStable;

        var tails = new List<int>();          // indexes into pairs
        var predecessor = new int[pairs.Count];

        for (int p = 0; p < pairs.Count; p++)
        {
            int right = pairs[p].Right;
            int lo = 0, hi = tails.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (pairs[tails[mid]].Right < right)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            predecessor[p] = lo > 0 ? tails[lo - 1] : -1;
            if (lo == tails.Count)
                tails.Add(p);
            else
                tails[lo] = p;
        }

        for (int p = tails[^1]; p >= 0; p = predecessor[p])
            isStable[p] = true;

        return isStable;
    }

    private static IEnumerable<(int Left, int Right)> StableAnchorsInOrder(List<(int Left, int Right)> pairs, bool[] isStable)
    {
        for (int p = 0; p < pairs.Count; p++)
        {
            if (isStable[p])
                yield return pairs[p];
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
        var gapLeft = new List<int>();
        for (int i = leftStart; i < leftEnd; i++)
        {
            if (!leftConsumed[i])
                gapLeft.Add(i);
        }

        var gapRight = new List<int>();
        for (int j = rightStart; j < rightEnd; j++)
        {
            if (rightKind[j] == ElementKind.Unassigned)
                gapRight.Add(j);
        }

        if (gapLeft.Count == 0 || gapRight.Count == 0)
            return;

        var script = MyersDiff(gapLeft, gapRight, leftHashes, rightHashes);
        if (script is null)
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

        foreach (var (li, rj) in script)
        {
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
    /// Greedy Myers over the two gap hash sequences, returning matched index pairs (into
    /// the gap lists) in order - or null when the edit distance exceeds
    /// <see cref="MaxMyersEditDistance"/> (caller falls back to positional pairing).
    /// </summary>
    private static List<(int Left, int Right)>? MyersDiff(List<int> gapLeft, List<int> gapRight, ulong[] leftHashes, ulong[] rightHashes)
    {
        int n = gapLeft.Count, m = gapRight.Count;
        int maxD = Math.Min(n + m, MaxMyersEditDistance);
        int offset = maxD;

        var v = new int[2 * maxD + 1];
        var trace = new List<int[]>();

        bool Equal(int i, int j) => leftHashes[gapLeft[i]] == rightHashes[gapRight[j]];

        int finalD = -1;
        for (int d = 0; d <= maxD && finalD < 0; d++)
        {
            trace.Add((int[])v.Clone());
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
        }

        if (finalD < 0)
            return null;

        // Backtrack, collecting the diagonal (match) runs.
        var matches = new List<(int Left, int Right)>();
        int cx = n, cy = m;
        for (int d = finalD; d > 0; d--)
        {
            var pv = trace[d];
            int k = cx - cy;
            int prevK = k == -d || (k != d && pv[offset + k - 1] < pv[offset + k + 1]) ? k + 1 : k - 1;
            int px = pv[offset + prevK];
            int py = px - prevK;

            while (cx > px && cy > py && cx > 0 && cy > 0)
            {
                matches.Add((cx - 1, cy - 1));
                cx--;
                cy--;
            }

            cx = px;
            cy = py;
        }

        while (cx > 0 && cy > 0)
        {
            matches.Add((cx - 1, cy - 1));
            cx--;
            cy--;
        }

        matches.Reverse();
        return matches;
    }

    // ── Cross-parent move reconciliation ───────────────────────────────────────────────

    /// <summary>
    /// Pairs Removed/Added container records with identical content hashes - exactly once
    /// on each side, the unique-anchor rule - and rewrites both in place to Moved, each
    /// carrying both token indexes and its partner's record index. Containers only: a
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

            ref var removedRecord = ref this.items.ItemRef(removed.RecordIndex);
            ref var addedRecord = ref this.items.ItemRef(added.RecordIndex);

            // Partner fields first, StatusBits last (release) - paired with GetRecord
            // reading StatusBits first (acquire).
            Volatile.Write(ref removedRecord.RightToken, addedRecord.RightToken);
            Volatile.Write(ref removedRecord.MovePartnerRecord, added.RecordIndex);
            Volatile.Write(ref addedRecord.LeftToken, removedRecord.LeftToken);
            Volatile.Write(ref addedRecord.MovePartnerRecord, removed.RecordIndex);
            Volatile.Write(ref removedRecord.StatusBits, (int)DiffStatus.Moved | FlagMoveSource | FlagCrossParentMove);
            Volatile.Write(ref addedRecord.StatusBits, (int)DiffStatus.Moved | FlagCrossParentMove);
        }
    }
}
