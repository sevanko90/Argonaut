using System;

namespace Argonaut.Engine.Indexing.Trees;

/// <summary>
/// The writing side of a <see cref="SparseContainerIndex"/>: a format's scanner reports the
/// document's structure in order, and this decides what to record. Single-threaded - the scan
/// body owns it.
///
/// Every container is tracked on a stack while open, recorded or not, because any of them may
/// yet reach the promotion size and the ordinals of their children are needed from the start.
/// The stack is as deep as the document, not as long.
/// </summary>
public sealed class SparseContainerIndexBuilder
{
    private struct Frame
    {
        public long Start;
        public long OrdinalInParent;
        public long LastCheckpoint;
        public long Ordinal;
        public int Record;
        public byte FormatKind;
    }

    private readonly SparseContainerIndex index;
    private Frame[] frames = new Frame[64];
    private int depth;

    // Frames below this are recorded; the rest are not yet. Promotion goes outward-in, so the
    // recorded frames are always a prefix of the stack.
    private int recordedDepth;

    // The top level's own child count, for ordinals of top-level containers (a document may hold
    // several top-level values, as NDJSON does).
    private long topLevelOrdinal;

    public SparseContainerIndexBuilder(SparseContainerIndex index) => this.index = index;

    public SparseContainerIndex Index => index;

    /// <summary>A container opens at <paramref name="offset"/>, its first byte. Its first child,
    /// if it has one, follows the opening without a separator.</summary>
    public void Open(long offset, byte formatKind)
    {
        Promote(offset);

        if (depth == frames.Length)
            Array.Resize(ref frames, frames.Length * 2);

        long ordinalInParent = depth == 0 ? topLevelOrdinal : frames[depth - 1].Ordinal;
        frames[depth++] = new Frame
        {
            Start = offset,
            OrdinalInParent = ordinalInParent,
            LastCheckpoint = offset,
            Ordinal = 0,
            Record = -1,
            FormatKind = formatKind,
        };
    }

    /// <summary>In the innermost open container, the previous child has ended and another
    /// follows; a parse may resume at <paramref name="resumeOffset"/>. At the top level (no
    /// container open) this separates top-level values.</summary>
    public void Separator(long resumeOffset)
    {
        Promote(resumeOffset);

        if (depth == 0)
        {
            topLevelOrdinal++;
            return;
        }

        ref var frame = ref frames[depth - 1];
        frame.Ordinal++;

        if (frame.Record >= 0 && resumeOffset - frame.LastCheckpoint >= index.CheckpointBytes)
        {
            index.checkpoints.Add(new TreeCheckpoint(resumeOffset, frame.Ordinal, frame.Record));
            frame.LastCheckpoint = resumeOffset;
        }
    }

    /// <summary>The innermost open container closes; <paramref name="end"/> is one past its last
    /// byte. <paramref name="isEmpty"/> when it had no children at all.</summary>
    public void Close(long end, bool isEmpty)
    {
        if (depth == 0)
            throw new InvalidOperationException("Close with no container open.");

        Promote(end);

        var frame = frames[--depth];
        if (recordedDepth > depth)
            recordedDepth = depth;

        if (frame.Record < 0)
            return;

        // Child count before end, read in the opposite order, so a reader that sees the end
        // also sees the count.
        ref var slot = ref index.containers.ItemRef(frame.Record);
        System.Threading.Volatile.Write(ref slot.ChildCount, isEmpty ? 0 : frame.Ordinal + 1);
        System.Threading.Volatile.Write(ref slot.End, end);
    }

    /// <summary>The scan has read up to <paramref name="offset"/> with no event to report -
    /// through a long string, say. Records any open container that has now grown large enough,
    /// and publishes how far the structure is known.</summary>
    public void Advance(long offset)
    {
        Promote(offset);
        index.PublishScannedTo(offset);
    }

    /// <summary>The document has ended at <paramref name="end"/>. Every container must have been
    /// closed.</summary>
    public void Complete(long end)
    {
        if (depth != 0)
            throw new InvalidOperationException($"{depth} container(s) still open at the end of the document.");

        index.PublishScannedTo(end);
        index.MarkComplete();
    }

    private void Promote(long offset)
    {
        while (recordedDepth < depth && offset - frames[recordedDepth].Start >= index.PromotionBytes)
        {
            ref var frame = ref frames[recordedDepth];
            frame.Record = index.containers.Add(new SparseContainerIndex.ContainerSlot
            {
                Start = frame.Start,
                End = -1,
                OrdinalInParent = frame.OrdinalInParent,
                ChildCount = -1,
                Parent = recordedDepth == 0 ? -1 : frames[recordedDepth - 1].Record,
                Depth = recordedDepth,
                FormatKind = frame.FormatKind,
            });
            recordedDepth++;
        }
    }
}
