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
        if (original.Length > 0)
            this.pieces.Add(new RawPiece(OriginalChunk, 0, original.Length, 0));

        Length = original.Length;
    }

    public long Length { get; private set; }

    /// <summary>True while the document still reads exactly as the file on disk does.</summary>
    public bool IsUnedited => this.pieces.Count <= 1 && Length == this.original.Length;

    /// <summary>
    /// Number of pieces. The row index watches this to decide when a full re-index is cheaper
    /// than carrying more shift entries.
    /// </summary>
    public int PieceCount => this.pieces.Count;

    /// <summary>
    /// Swaps the original buffer for an equivalent one - a re-opened mapping of the same bytes
    /// after a save unmapped the old one. Rejects a replacement of a different length, because
    /// every piece offset into the original would then mean something different.
    /// </summary>
    public void RepointOriginal(IByteSource replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (replacement.Length != this.original.Length)
            throw new ArgumentException(
                $"Replacement is {replacement.Length} bytes; the original was {this.original.Length}. " +
                "Piece offsets are only meaningful against bytes of the same length.",
                nameof(replacement));

        this.original = replacement;
    }

    public ReadOnlySpan<byte> GetContiguousSpan(long offset, int maxLength)
    {
        if (offset < 0 || offset >= Length || maxLength <= 0)
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
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, Length);

        if (bytes.IsEmpty)
            return new RawEditExtent(offset, 0, 0);

        int at = SplitAt(offset);
        this.pieces.InsertRange(at, AppendToScratch(bytes));
        Length += bytes.Length;
        RenumberFrom(at);

        return new RawEditExtent(offset, 0, bytes.Length);
    }

    /// <summary>Removes <paramref name="length"/> bytes from <paramref name="offset"/>.</summary>
    public RawEditExtent Delete(long offset, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset + length, Length);

        if (length == 0)
            return new RawEditExtent(offset, 0, 0);

        int from = SplitAt(offset);
        int to = SplitAt(offset + length);
        this.pieces.RemoveRange(from, to - from);
        Length -= length;
        RenumberFrom(from);

        return new RawEditExtent(offset, length, 0);
    }

    /// <summary>
    /// Replaces a range in one step, so undo treats it as one action rather than a delete the
    /// user has to undo separately from the insert that followed it.
    /// </summary>
    public RawEditExtent Replace(long offset, long length, ReadOnlySpan<byte> bytes)
    {
        Delete(offset, length);
        Insert(offset, bytes);
        return new RawEditExtent(offset, length, bytes.Length);
    }

    /// <summary>
    /// Copy of the current piece list, for undo. Undo restores a whole snapshot rather than
    /// inverting each edit: a piece is a few dozen bytes and the list is bounded by the rebuild
    /// threshold, so a deep undo stack costs kilobytes - against the alternative of retaining
    /// deleted <i>bytes</i>, where selecting a whole multi-GB document and pressing delete would
    /// have to keep all of it alive to be undoable. Scratch is append-only and never reclaimed,
    /// so bytes an undone edit referenced are still there when redo needs them.
    /// </summary>
    internal object Snapshot() => new SnapshotState(this.pieces.ToArray(), Length);

    /// <summary>Restores a <see cref="Snapshot"/>.</summary>
    internal void Restore(object snapshot)
    {
        var state = (SnapshotState)snapshot;
        this.pieces.Clear();
        this.pieces.AddRange(state.Pieces);
        Length = state.Length;
    }

    private sealed record SnapshotState(RawPiece[] Pieces, long Length);

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
        if (offset == Length)
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
