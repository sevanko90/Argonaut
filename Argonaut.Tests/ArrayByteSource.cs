using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// An <see cref="IByteSource"/> over a plain array, so byte-layer tests need no temp file and no
/// mapping. Serves every request whole, exactly as <see cref="MMapFile"/> does - a test wanting
/// the split-request behaviour builds a <see cref="Argonaut.Features.Raw.RawPieceTable"/> over
/// one of these instead.
/// </summary>
internal sealed class ArrayByteSource : IByteSource
{
    private readonly byte[] bytes;

    public ArrayByteSource(byte[] bytes) => this.bytes = bytes;

    public long AvailableLength => this.bytes.Length;

    public ReadOnlySpan<byte> GetContiguousSpan(long offset, int maxLength)
        => offset < 0 || offset >= AvailableLength || maxLength <= 0
            ? ReadOnlySpan<byte>.Empty
            : this.bytes.AsSpan((int)offset, (int)Math.Min(maxLength, AvailableLength - offset));

    public int CopyTo(long offset, Span<byte> destination)
    {
        var span = GetContiguousSpan(offset, destination.Length);
        span.CopyTo(destination);
        return span.Length;
    }
}
