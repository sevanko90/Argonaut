using System;
using System.Buffers;
using System.Collections.Generic;

namespace Argonaut.Engine.Collections;

/// <summary>One filled buffer of a <see cref="ChunkedByteWriter"/>: the array and how much of
/// it was written.</summary>
public readonly record struct FilledChunk(byte[] Buffer, int Length)
{
    public ReadOnlySpan<byte> Span => Buffer.AsSpan(0, Length);
}

/// <summary>
/// A buffer writer that never copies what it has written: output goes into fixed-size arrays,
/// and a full one is kept rather than grown into a bigger one. So producing N bytes costs N
/// bytes, not the up-to-3N of a doubling buffer, no single allocation is larger than a chunk
/// (or one oversized request), and the arrays themselves can be handed on - the raw editor
/// adopts them as its scratch without a second copy.
///
/// The arrays are not pooled, precisely because they are handed on and outlive the writer.
/// </summary>
public sealed class ChunkedByteWriter : IBufferWriter<byte>
{
    public const int DefaultChunkBytes = 64 * 1024;

    private readonly int chunkBytes;
    private readonly List<FilledChunk> chunks = new();
    private byte[]? current;
    private int filled;

    public ChunkedByteWriter(int chunkBytes = DefaultChunkBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkBytes, 16);
        this.chunkBytes = chunkBytes;
    }

    /// <summary>Bytes written so far.</summary>
    public long WrittenLength { get; private set; }

    /// <summary>The written bytes, in order. Valid until the next write.</summary>
    public IReadOnlyList<FilledChunk> Chunks
    {
        get
        {
            Seal();
            return this.chunks;
        }
    }

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (this.current is null || this.filled + count > this.current.Length)
            throw new InvalidOperationException("Advanced past the span handed out.");

        this.filled += count;
        WrittenLength += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0) => Reserve(sizeHint).AsMemory(this.filled);

    public Span<byte> GetSpan(int sizeHint = 0) => Reserve(sizeHint).AsSpan(this.filled);

    private byte[] Reserve(int sizeHint)
    {
        int wanted = Math.Max(sizeHint, 1);
        if (this.current is not null && this.current.Length - this.filled < wanted)
            Seal();

        if (this.current is null || this.current.Length - this.filled < wanted)
            this.current = new byte[Math.Max(this.chunkBytes, wanted)];

        return this.current;
    }

    /// <summary>Moves the chunk being filled onto <see cref="chunks"/>, if it holds anything.
    /// Writing afterwards starts a fresh chunk, so a handed-out chunk is never written again.</summary>
    private void Seal()
    {
        if (this.current is null || this.filled == 0)
            return;

        this.chunks.Add(new FilledChunk(this.current, this.filled));
        this.current = null;
        this.filled = 0;
    }
}
