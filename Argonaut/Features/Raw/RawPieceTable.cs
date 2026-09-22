using System;
using System.Collections.Generic;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Raw;

/// <summary>
/// What one edit did to the document, in logical byte terms. Enough for the row index to work
/// out which rows moved (see RawRowShiftLog) without knowing anything about pieces.
/// </summary>
public readonly record struct RawEditExtent(long Offset, long BytesRemoved, long BytesInserted)
{
    public long ByteDelta => BytesInserted - BytesRemoved;
}

/// <summary>
/// The document as an ordered list of pieces over two buffers: the original bytes (a mapping,
/// untouched and never written) and an append-only scratch buffer holding everything typed or
/// pasted. An edit splits at most two pieces and inserts one; no byte is ever moved or mutated,
/// so memory is proportional to the number of edits rather than to the size of the file - the
/// whole reason a multi-GB document can be edited at all. See docs/editing-options.md §1D.
///
/// A logical offset resolves to a physical one by binary search over the piece list, which is
/// the one cost every read pays. The list is as long as the number of edits, so that is a
/// handful of comparisons; the row index collapses it back to a single piece by rebuilding
/// once the list grows past its threshold.
///
/// <b>The original is held behind <see cref="RepointOriginal"/> rather than as a fixed
/// reference</b>: saving unmaps the file before the rename, and a rename that then fails has to
/// leave the user's edits intact over a freshly-opened mapping of the same bytes.
///
/// Not thread-safe: edits and reads happen on the UI thread. Background readers are given the
/// table only once editing is quiescent.
/// </summary>
public sealed class RawPieceTable : IByteSource
{
    /// <summary><see cref="RawPiece.ChunkIndex"/> value meaning "the original bytes".</summary>
    private const int OriginalChunk = -1;

    /// <summary>
    /// Scratch is chunked rather than one doubling array so a large paste never copies the whole
    /// buffer, and so no single allocation lands on the large object heap unless the paste itself
    /// is that big. Every append lands wholly inside one chunk, which keeps the useful invariant
    /// that a piece is always physically contiguous - so <see cref="GetContiguousSpan"/> only
    /// ever truncates at a piece boundary.
    /// </summary>
    private const int ScratchChunkSize = 64 * 1024;

    /// <summary>One run of bytes from one buffer. <see cref="ChunkIndex"/> is
    /// <see cref="OriginalChunk"/> for the original, otherwise an index into the scratch chunks;
    /// <see cref="Offset"/> is within that buffer, and <see cref="LogicalStart"/> is where the
    /// run begins in the document.</summary>
    private readonly record struct RawPiece(int ChunkIndex, long Offset, long Length, long LogicalStart);

    private readonly List<byte[]> scratchChunks = new();
    private readonly List<RawPiece> pieces = new();

    private IByteSource original;
    private int scratchFill; // bytes used in the last scratch chunk

    public RawPieceTable(IByteSource original)
    {
        this.original = original;
        if (original.AvailableLength > 0)
            this.pieces.Add(new RawPiece(OriginalChunk, 0, original.AvailableLength, 0));

        AvailableLength = original.AvailableLength;
    }

    public long AvailableLength { get; private set; }

    /// <summary>True while the document still reads exactly as the file on disk does.</summary>
    public bool IsUnedited => this.pieces.Count <= 1 && AvailableLength == this.original.AvailableLength;

    /// <summary>
    /// Number of pieces. The row index watches this to decide when a full re-index is cheaper
    /// than carrying more shift entries.
    /// </summary>
    public int PieceCount => this.pieces.Count;

    /// <summary>
    /// Where the document differs from the original, in document order: every run of inserted
    /// bytes as its range, and every place original bytes were removed as an empty range at the
    /// seam. A deletion leaves no scratch piece behind - only two original pieces that no longer
    /// meet - so reading the scratch pieces alone would miss every delete.
    ///
    /// A struct enumerator over the live piece list: no allocation, and not valid across an edit.
    /// </summary>
    internal EditedRangeEnumerator EnumerateEditedRanges() => new(this);

    /// <summary>See <see cref="EnumerateEditedRanges"/>.</summary>
    internal struct EditedRangeEnumerator
    {
        private readonly RawPieceTable table;
        private int next;
        private long originalReached;
        private bool endChecked;

        public EditedRangeEnumerator(RawPieceTable table)
        {
            this.table = table;
            this.next = 0;
            this.originalReached = 0;
            this.endChecked = false;
            Current = default;
        }

        /// <summary>Logical range; <c>Start == End</c> for a deletion seam.</summary>
        public (long Start, long End) Current { get; private set; }

        public readonly EditedRangeEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            var pieces = this.table.pieces;
            while (this.next < pieces.Count)
            {
                var piece = pieces[this.next++];
                if (piece.ChunkIndex != OriginalChunk)
                {
                    Current = (piece.LogicalStart, piece.LogicalStart + piece.Length);
                    return true;
                }

                // Original pieces only ever appear in increasing buffer order, so a gap between
                // where the last one ended and where this one starts is bytes that were removed.
                bool removedBefore = piece.Offset != this.originalReached;
                this.originalReached = piece.Offset + piece.Length;
                if (removedBefore)
                {
                    Current = (piece.LogicalStart, piece.LogicalStart);
                    return true;
                }
            }

            // Bytes removed from the end of the original leave no piece to notice them by.
            if (!this.endChecked)
            {
                this.endChecked = true;
                if (this.originalReached != this.table.original.AvailableLength)
                {
                    Current = (this.table.AvailableLength, this.table.AvailableLength);
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Swaps the original buffer for an equivalent one - a re-opened mapping of the same bytes
    /// after a save unmapped the old one. Rejects a replacement of a different length, because
    /// every piece offset into the original would then mean something different.
    /// </summary>
    public void RepointOriginal(IByteSource replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (replacement.AvailableLength != this.original.AvailableLength)
            throw new ArgumentException(
                $"Replacement is {replacement.AvailableLength} bytes; the original was {this.original.AvailableLength}. " +
                "Piece offsets are only meaningful against bytes of the same length.",
                nameof(replacement));

        this.original = replacement;
    }

    public ReadOnlySpan<byte> GetContiguousSpan(long offset, int maxLength)
    {
        if (offset < 0 || offset >= AvailableLength || maxLength <= 0)
            return ReadOnlySpan<byte>.Empty;

        int pieceIndex = FindPiece(offset);
        var piece = this.pieces[pieceIndex];
        long within = offset - piece.LogicalStart;
        int available = (int)Math.Min(maxLength, piece.Length - within);

        return piece.ChunkIndex == OriginalChunk
            ? this.original.GetContiguousSpan(piece.Offset + within, available)
            : this.scratchChunks[piece.ChunkIndex].AsSpan((int)(piece.Offset + within), available);
    }

    public int CopyTo(long offset, Span<byte> destination)
    {
        int copied = 0;
        while (copied < destination.Length)
        {
            var span = GetContiguousSpan(offset + copied, destination.Length - copied);
            if (span.IsEmpty)
                break;

            span.CopyTo(destination[copied..]);
            copied += span.Length;
        }

        return copied;
    }

    /// <summary>Inserts <paramref name="bytes"/> so that they begin at <paramref name="offset"/>.</summary>
    public RawEditExtent Insert(long offset, ReadOnlySpan<byte> bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, AvailableLength);

        if (bytes.IsEmpty)
            return new RawEditExtent(offset, 0, 0);

        BeginChange(offset, 0);
        InsertCore(offset, bytes);
        EndChange();
        return new RawEditExtent(offset, 0, bytes.Length);
    }

    private void InsertCore(long offset, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return;

        if (!TryExtendScratchRun(offset, bytes))
        {
            int at = SplitAt(offset);
            this.pieces.InsertRange(at, AppendToScratch(bytes));
            RenumberFrom(at);
        }

        AvailableLength += bytes.Length;
    }

    /// <summary>
    /// Grows the scratch piece an insertion continues, instead of describing the new bytes as a
    /// piece of their own.
    ///
    /// Typing is a run of one-byte insertions each starting exactly where the last one ended, so
    /// without this every keystroke costs a piece: four hundred characters typed became four
    /// hundred pieces, each one byte long. That is not just untidy. Every read through the table
    /// is a binary search over the list, and worse, <see cref="GetContiguousSpan"/> truncates at
    /// every piece boundary - so a row scan across a typed run degenerates from a vectorized walk
    /// to one byte per call, and each of those bytes pays a fresh binary search to be found.
    ///
    /// Four conditions, and each one is what keeps the extension indistinguishable from the
    /// insertion it replaces: the run must be the tail of the chunk currently being filled, the
    /// insertion must continue it exactly, the piece must still end where scratch does (an undo
    /// rewinds the piece list but never scratch, so a piece can outlive being the tail), and the
    /// bytes must fit in the chunk, because a piece is always physically contiguous.
    /// </summary>
    private bool TryExtendScratchRun(long offset, ReadOnlySpan<byte> bytes)
    {
        if (offset == 0 || this.pieces.Count == 0 || this.scratchChunks.Count == 0)
            return false;

        int index = FindPiece(offset - 1);
        var piece = this.pieces[index];

        if (piece.ChunkIndex != this.scratchChunks.Count - 1)
            return false;

        if (piece.LogicalStart + piece.Length != offset)
            return false;

        if (piece.Offset + piece.Length != this.scratchFill)
            return false;

        var chunk = this.scratchChunks[^1];
        if (chunk.Length - this.scratchFill < bytes.Length)
            return false;

        bytes.CopyTo(chunk.AsSpan(this.scratchFill));
        this.scratchFill += bytes.Length;

        // A record struct, so every snapshot the journal took holds its own copy of the shorter
        // piece - undo is unaffected by the run growing after it was recorded.
        this.pieces[index] = piece with { Length = piece.Length + bytes.Length };
        RenumberFrom(index + 1);
        return true;
    }

    /// <summary>Removes <paramref name="length"/> bytes from <paramref name="offset"/>.</summary>
    public RawEditExtent Delete(long offset, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset + length, AvailableLength);

        if (length == 0)
            return new RawEditExtent(offset, 0, 0);

        BeginChange(offset, length);
        DeleteCore(offset, length);
        EndChange();
        return new RawEditExtent(offset, length, 0);
    }

    private void DeleteCore(long offset, long length)
    {
        if (length == 0)
            return;

        int from = SplitAt(offset);
        int to = SplitAt(offset + length);
        this.pieces.RemoveRange(from, to - from);
        AvailableLength -= length;
        RenumberFrom(from);
    }

    /// <summary>
    /// Replaces a range in one step, so undo treats it as one action rather than a delete the
    /// user has to undo separately from the insert that followed it.
    /// </summary>
    public RawEditExtent Replace(long offset, long length, ReadOnlySpan<byte> bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset + length, AvailableLength);

        // One recorded change for the pair, not two. The delete and the insert act at the same
        // offset and therefore on the same run of the piece list, so undo restores both together
        // - which is what makes a replacement one action to the user rather than two.
        BeginChange(offset, length);
        DeleteCore(offset, length);
        InsertCore(offset, bytes);
        EndChange();
        return new RawEditExtent(offset, length, bytes.Length);
    }

    /// <summary>Scratch chunks allocated so far.</summary>
    internal int ScratchChunkCount => this.scratchChunks.Count;

    /// <summary>
    /// Bytes of scratch in use. The memory an edit session actually costs, against a file whose
    /// size it is deliberately independent of - which is the claim the whole structure is here to
    /// make, and the one worth being able to watch.
    /// </summary>
    internal long ScratchBytesUsed
    {
        get
        {
            long total = 0;
            for (int i = 0; i < this.scratchChunks.Count - 1; i++)
                total += this.scratchChunks[i].Length;

            return this.scratchChunks.Count == 0 ? 0 : total + this.scratchFill;
        }
    }

    /// <summary>
    /// The piece list, flattened for display. Takes at most <paramref name="max"/> from the
    /// start and reports how many it left out, so a document edited in a thousand places does
    /// not build a thousand-entry list per keystroke for a window showing the first screenful.
    /// </summary>
    internal IReadOnlyList<RawPieceSnapshot> DescribePieces(int max, out int omitted)
    {
        int shown = Math.Min(max, this.pieces.Count);
        omitted = this.pieces.Count - shown;

        var described = new List<RawPieceSnapshot>(shown);
        for (int i = 0; i < shown; i++)
        {
            var piece = this.pieces[i];
            described.Add(new RawPieceSnapshot(
                i,
                piece.ChunkIndex == OriginalChunk,
                piece.ChunkIndex,
                piece.Offset,
                piece.Length,
                piece.LogicalStart));
        }

        return described;
    }

    /// <summary>
    /// The run of the piece list one edit rewrote, as it was and as it became. Undo swaps one for
    /// the other.
    ///
    /// This used to be a copy of the <i>whole</i> piece list per edit, on the reasoning that a
    /// piece is a few dozen bytes and the list is bounded - and the list is not bounded. Editing
    /// in n places leaves about 2n pieces, so snapshotting all of them n times is O(n²): measured
    /// at 4.4MB for 250 edits, 65MB for 1,000 and 187MB for 2,000, against 1.5MB for the piece
    /// table and row index together. A run is proportional to what the edit actually disturbed,
    /// which for typing is one piece.
    ///
    /// Bytes are still never retained: scratch is append-only and never reclaimed, so what an
    /// undone edit referenced is still there when redo points at it again. That is what stops
    /// selecting a multi-GB document and pressing delete from having to keep all of it alive.
    /// </summary>
    private sealed record PieceRun(int Index, RawPiece[] Before, RawPiece[] After, long LengthBefore, long LengthAfter);

    private PieceRun? lastChange;
    private int changeIndex;
    private int changePieceCount;
    private long changeLength;
    private RawPiece[] changeBefore = Array.Empty<RawPiece>();

    /// <summary>
    /// Notes the run of pieces an edit over <paramref name="removedLength"/> bytes at
    /// <paramref name="offset"/> can touch, before it touches them.
    ///
    /// Deliberately a superset: it starts one byte early, because a run of typed bytes grows the
    /// piece <i>before</i> the insertion point rather than splitting the one after it, and it
    /// ends on the piece holding the last removed byte, which a split will divide. Recording a
    /// piece that turns out not to have changed costs a few dozen bytes and restores identically;
    /// missing one that did is a corrupt undo.
    /// </summary>
    private void BeginChange(long offset, long removedLength)
    {
        this.changeLength = AvailableLength;
        this.changePieceCount = this.pieces.Count;

        if (this.pieces.Count == 0)
        {
            this.changeIndex = 0;
            this.changeBefore = Array.Empty<RawPiece>();
            return;
        }

        int first = offset > 0 ? FindPiece(Math.Min(offset - 1, AvailableLength - 1)) : 0;
        int last = FindPiece(Math.Clamp(offset + removedLength, 0, AvailableLength - 1));

        this.changeIndex = first;
        this.changeBefore = this.pieces.GetRange(first, Math.Max(last - first + 1, 0)).ToArray();
    }

    /// <summary>
    /// Reads back what the run became. Only indices at or after the run's start can have moved,
    /// so how many pieces it now spans follows from how the list's length changed.
    /// </summary>
    private void EndChange()
    {
        int after = Math.Max(0, this.changeBefore.Length + (this.pieces.Count - this.changePieceCount));
        this.lastChange = new PieceRun(
            this.changeIndex,
            this.changeBefore,
            this.pieces.GetRange(this.changeIndex, after).ToArray(),
            this.changeLength,
            AvailableLength);
    }

    /// <summary>What the most recent edit did, for <see cref="RawEditJournal"/> to hold on to.
    /// Opaque, so the piece list stays this class's own business.</summary>
    internal object LastChange() => this.lastChange
        ?? throw new InvalidOperationException("No edit has been made to record.");

    /// <summary>Puts the run back as it was.</summary>
    internal void UndoChange(object change) => Swap(change, undo: true);

    /// <summary>Puts the run back as the edit left it.</summary>
    internal void RedoChange(object change) => Swap(change, undo: false);

    private void Swap(object change, bool undo)
    {
        var run = (PieceRun)change;
        var (remove, restore) = undo ? (run.After, run.Before) : (run.Before, run.After);

        this.pieces.RemoveRange(run.Index, remove.Length);
        this.pieces.InsertRange(run.Index, restore);
        AvailableLength = undo ? run.LengthBefore : run.LengthAfter;
        RenumberFrom(run.Index);
    }

    /// <summary>
    /// Index of the piece containing <paramref name="offset"/>. Binary search - the one cost
    /// every read through the table pays.
    /// </summary>
    private int FindPiece(long offset)
    {
        int lo = 0, hi = this.pieces.Count - 1;
        while (lo < hi)
        {
            int mid = lo + (hi - lo + 1) / 2;
            if (this.pieces[mid].LogicalStart <= offset)
                lo = mid;
            else
                hi = mid - 1;
        }

        return lo;
    }

    /// <summary>
    /// Ensures a piece boundary falls exactly at <paramref name="offset"/>, splitting the piece
    /// that straddles it, and returns the index of the piece that now starts there (== Count when
    /// the offset is the end of the document).
    /// </summary>
    private int SplitAt(long offset)
    {
        if (offset == AvailableLength)
            return this.pieces.Count;

        int index = FindPiece(offset);
        var piece = this.pieces[index];
        if (piece.LogicalStart == offset)
            return index;

        long head = offset - piece.LogicalStart;
        this.pieces[index] = piece with { Length = head };
        this.pieces.Insert(index + 1, new RawPiece(
            piece.ChunkIndex,
            piece.Offset + head,
            piece.Length - head,
            offset));

        return index + 1;
    }

    /// <summary>
    /// Copies <paramref name="bytes"/> into scratch and describes them as pieces. Normally one
    /// piece; a paste larger than a chunk becomes one chunk of its own, so it is still one piece.
    /// </summary>
    private List<RawPiece> AppendToScratch(ReadOnlySpan<byte> bytes)
    {
        var appended = new List<RawPiece>(1);
        while (!bytes.IsEmpty)
        {
            if (this.scratchChunks.Count == 0 || this.scratchFill == this.scratchChunks[^1].Length)
            {
                this.scratchChunks.Add(new byte[Math.Max(ScratchChunkSize, bytes.Length)]);
                this.scratchFill = 0;
            }

            var chunk = this.scratchChunks[^1];
            int room = Math.Min(chunk.Length - this.scratchFill, bytes.Length);
            bytes[..room].CopyTo(chunk.AsSpan(this.scratchFill));

            // LogicalStart is filled in by RenumberFrom once the pieces are spliced in.
            appended.Add(new RawPiece(this.scratchChunks.Count - 1, this.scratchFill, room, 0));
            this.scratchFill += room;
            bytes = bytes[room..];
        }

        return appended;
    }

    /// <summary>
    /// Recomputes <see cref="RawPiece.LogicalStart"/> from <paramref name="index"/> onwards.
    /// O(pieces after the edit) rather than O(1), which is the deliberate trade for keeping the
    /// starts materialised so every <i>read</i> is a plain binary search. Reads vastly outnumber
    /// edits, and the piece list is bounded by the row index's rebuild threshold.
    /// </summary>
    private void RenumberFrom(int index)
    {
        long start = index == 0 ? 0 : this.pieces[index - 1].LogicalStart + this.pieces[index - 1].Length;
        for (int i = index; i < this.pieces.Count; i++)
        {
            this.pieces[i] = this.pieces[i] with { LogicalStart = start };
            start += this.pieces[i].Length;
        }
    }
}
