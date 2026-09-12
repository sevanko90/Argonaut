using System;

namespace Argonaut.Infrastructure;

/// <summary>
/// A document held in a managed array - a clipboard paste, or a download small enough not to be
/// worth spilling to a temp file. One buffer, fully present, so it serves every request whole and
/// its length is settled from birth, exactly like <see cref="MMapFile"/> and unlike a piece table.
///
/// A window over a larger array rather than always the whole of one, so
/// <see cref="MemoryByteOrigin.OpenRange"/> can hand out a sub-document without copying: an
/// NDJSON line inside a pasted payload is a view, not a second array.
///
/// Holds no OS resource, so it does not implement <see cref="IDisposable"/> and
/// <see cref="ByteSourceReading.Release"/> over it is a no-op. The array stays alive as long as
/// the origin that owns it.
/// </summary>
public sealed class MemoryByteSource : IByteSource
{
    private readonly byte[] buffer;
    private readonly int start;
    private readonly int length;

    public MemoryByteSource(byte[] buffer)
        : this(buffer, 0, (buffer ?? throw new ArgumentNullException(nameof(buffer))).Length)
    {
    }

    public MemoryByteSource(byte[] buffer, int start, int length)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (start + length > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(length),
                $"Window [{start}, {start + length}) does not fit a {buffer.Length}-byte buffer.");

        this.buffer = buffer;
        this.start = start;
        this.length = length;
    }

    public long AvailableLength => this.length;

    public ReadOnlySpan<byte> GetContiguousSpan(long offset, int maxLength)
        => offset < 0 || offset >= this.length || maxLength <= 0
            ? ReadOnlySpan<byte>.Empty
            : this.buffer.AsSpan(this.start + (int)offset, Math.Min(maxLength, this.length - (int)offset));

    public int CopyTo(long offset, Span<byte> destination)
    {
        var span = GetContiguousSpan(offset, destination.Length);
        span.CopyTo(destination);
        return span.Length;
    }
}
