using System.Threading;
using Argonaut.Engine.Collections;

namespace Argonaut.Engine.Indexing.Trees;

/// <summary>
/// A sparse index over a tree-shaped document - JSON, and later XML - that records only what a
/// viewer needs to jump into the bytes, and leaves everything between to be re-parsed on demand.
/// Its size depends on the file's size, not on how many nodes it holds.
///
/// Two append-only logs, both in offset order:
/// <list type="bullet">
/// <item><b>Containers</b> whose bytes reach <see cref="PromotionBytes"/>. One is recorded as soon
/// as the scan passes that far into it, while it is still open - the root array of a multi-GB
/// file closes at the last byte, and a viewer needs to jump into it long before. Ancestors cross
/// the threshold before descendants, so a parent is always recorded before its children and the
/// log is sorted by start. Smaller containers are never recorded; there are at most
/// file / <see cref="PromotionBytes"/> recorded containers that do not enclose another.</item>
/// <item><b>Checkpoints</b>: at most one per <see cref="CheckpointBytes"/> of a recorded
/// container's own children, each a child start with its ordinal. A checkpoint is always taken in
/// the innermost open container.</item>
/// </list>
///
/// Format-agnostic: it knows byte ranges, nesting and ordinals, never syntax. A format's scanner
/// drives it through <see cref="SparseContainerIndexBuilder"/> and, when it resumes a parse, knows
/// how to read the container opening a <see cref="TreeResumePoint"/> may start at.
///
/// Single writer, lock-free readers, the same publication rules as every append-log index (see
/// <see cref="SegmentedAppendLog{T}"/>). A container's <c>End</c> and <c>ChildCount</c> are
/// written after it is published, so both sides access them volatile, child count first on write
/// and end first on read.
/// </summary>
public sealed class SparseContainerIndex
{
    internal struct ContainerSlot
    {
        public long Start;
        public long End;
        public long OrdinalInParent;
        public long ChildCount;
        public int Parent;
        public int Depth;
        public byte FormatKind;
    }

    internal readonly SegmentedAppendLog<ContainerSlot> containers = new();
    internal readonly SegmentedAppendLog<TreeCheckpoint> checkpoints = new();
    private long scannedTo;
    private volatile bool complete;

    public SparseContainerIndex(int promotionBytes, int checkpointBytes)
    {
        PromotionBytes = promotionBytes;
        CheckpointBytes = checkpointBytes;
    }

    /// <summary>A container is recorded once the scan is this many bytes past its start.</summary>
    public int PromotionBytes { get; }

    /// <summary>The least distance between consecutive checkpoints of one container.</summary>
    public int CheckpointBytes { get; }

    public int ContainerCount => containers.Count;

    public int CheckpointCount => checkpoints.Count;

    /// <summary>How far the structure is known: every container that encloses an offset below
    /// this, and is large enough, has been recorded.</summary>
    public long ScannedTo => Volatile.Read(ref scannedTo);

    /// <summary>True once the scan has reached the end of the document and closed everything it
    /// opened.</summary>
    public bool IsComplete => complete;

    public TreeContainer GetContainer(int container)
    {
        ref var slot = ref containers.ItemRef(container);
        long end = Volatile.Read(ref slot.End);
        long childCount = end < 0 ? -1 : Volatile.Read(ref slot.ChildCount);
        return new TreeContainer(slot.Start, end, slot.Parent, slot.Depth, slot.OrdinalInParent, childCount, slot.FormatKind);
    }

    public TreeCheckpoint GetCheckpoint(int checkpoint) => checkpoints.ItemRef(checkpoint);

    /// <summary>The innermost recorded container whose bytes include <paramref name="offset"/>
    /// (its start included, its end not), or -1.</summary>
    public int FindInnermostContainer(long offset)
    {
        // The last container starting at or before the offset is the innermost candidate: any
        // recorded container enclosing the offset encloses that one's start too, so it is that
        // one or one of its ancestors.
        int candidate = LastContainerStartingAtOrBefore(offset);
        while (candidate >= 0)
        {
            ref var slot = ref containers.ItemRef(candidate);
            long end = Volatile.Read(ref slot.End);
            if (end < 0 || offset < end)
                return candidate;

            candidate = slot.Parent;
        }

        return -1;
    }

    /// <summary>The recorded container whose first byte is <paramref name="start"/>, or -1 if
    /// that container is not recorded (yet).</summary>
    public int FindContainerStartingAt(long start)
    {
        int candidate = LastContainerStartingAtOrBefore(start);
        return candidate >= 0 && containers.ItemRef(candidate).Start == start ? candidate : -1;
    }

    /// <summary>
    /// Where to start parsing to reach <paramref name="offset"/>: in the innermost recorded
    /// container enclosing it, at the latest known child start at or before it. That is its own
    /// latest checkpoint, or the end of a recorded child - which is where its next sibling starts -
    /// or failing both, the container's opening.
    /// </summary>
    public TreeResumePoint FindResumePoint(long offset)
    {
        int container = FindInnermostContainer(offset);
        if (container < 0)
            return new TreeResumePoint(-1, 0, 0, AtOpen: false);

        long start = containers.ItemRef(container).Start;
        int checkpoint = LastCheckpointAtOrBefore(offset);
        // A checkpoint at the container's own start is its parent's - the child it marks is this
        // container - so only one strictly after the start is inside.
        if (checkpoint < 0 || checkpoints.ItemRef(checkpoint).Offset <= start)
            return new TreeResumePoint(container, start, 0, AtOpen: true);

        // The checkpoint is inside the container (it is after the container's start and at or
        // before an offset the container encloses), so it is the container's own or a
        // descendant's.
        return ResumeFrom(checkpoint, container);
    }

    /// <summary>
    /// Where to start parsing to reach child <paramref name="ordinal"/> of
    /// <paramref name="container"/>: the latest known child start at or before it. The parser
    /// then skips <c>ordinal - result.Ordinal</c> children.
    /// </summary>
    public TreeResumePoint FindResumePoint(int container, long ordinal)
    {
        long start = containers.ItemRef(container).Start;
        long end = Volatile.Read(ref containers.ItemRef(container).End);

        // Checkpoints inside the container - strictly after its start, see above - are contiguous
        // in the log. Over them the ordinal *in this container* never decreases - a descendant's
        // checkpoint counts as the child after the one it is inside - so a binary search on it
        // finds the last one not past the target.
        int first = LastCheckpointAtOrBefore(start) + 1;
        int last = end < 0 ? checkpoints.Count - 1 : LastCheckpointAtOrBefore(end - 1);
        var best = new TreeResumePoint(container, start, 0, AtOpen: true);

        int low = first, high = last;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            var point = ResumeFrom(middle, container);
            if (point.Ordinal <= ordinal)
            {
                best = point;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return best;
    }

    /// <summary>The resume point in <paramref name="container"/> a checkpoint inside it gives:
    /// the checkpoint itself if it is the container's own, else the end of the container's child
    /// that holds it.</summary>
    private TreeResumePoint ResumeFrom(int checkpoint, int container)
    {
        var point = checkpoints.ItemRef(checkpoint);
        if (point.Container == container)
            return new TreeResumePoint(container, point.Offset, point.Ordinal, AtOpen: false);

        int child = point.Container;
        while (true)
        {
            ref var slot = ref containers.ItemRef(child);
            if (slot.Parent == container)
            {
                long childEnd = Volatile.Read(ref slot.End);
                if (childEnd < 0)
                {
                    // Still open, so the target is not past it - this only happens for an
                    // ordinal search reaching into a child still being scanned. Its start is
                    // the latest point known in the container.
                    return new TreeResumePoint(container, slot.Start, slot.OrdinalInParent, AtOpen: false);
                }

                return new TreeResumePoint(container, childEnd, slot.OrdinalInParent + 1, AtOpen: false);
            }

            child = slot.Parent;
        }
    }

    private int LastContainerStartingAtOrBefore(long offset)
    {
        int low = 0, high = containers.Count - 1, found = -1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            if (containers.ItemRef(middle).Start <= offset)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found;
    }

    private int LastCheckpointAtOrBefore(long offset)
    {
        int low = 0, high = checkpoints.Count - 1, found = -1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            if (checkpoints.ItemRef(middle).Offset <= offset)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found;
    }

    internal void PublishScannedTo(long offset) => Volatile.Write(ref scannedTo, offset);

    internal void MarkComplete() => complete = true;
}
