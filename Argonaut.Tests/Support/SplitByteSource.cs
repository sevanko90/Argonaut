using Argonaut.Engine.Bytes;

namespace Argonaut.Tests.Support;

/// <summary>
/// An <see cref="IByteSource"/> that serves its bytes in fixed-size pieces - every
/// <see cref="IByteSource.GetContiguousSpan"/> stops at the next multiple of the piece size - so a
/// scanner's handling of a range split across buffers runs on every piece boundary rather than
/// only where a piece table happens to have been edited.
/// </summary>
internal sealed class SplitByteSource(byte[] all, int pieceSize) : IByteSource
{
    public long AvailableLength => all.Length;

    public ReadOnlySpan<byte> GetContiguousSpan(long offset, int maxLength)
    {
        if (offset < 0 || offset >= all.Length || maxLength <= 0)
            return ReadOnlySpan<byte>.Empty;

        long pieceEnd = (offset / pieceSize + 1) * pieceSize;
        int length = (int)Math.Min(Math.Min(maxLength, pieceEnd - offset), all.Length - offset);
        return all.AsSpan((int)offset, length);
    }

    public int CopyTo(long offset, Span<byte> destination)
    {
        int copied = 0;
        while (copied < destination.Length)
        {
            var span = GetContiguousSpan(offset + copied, destination.Length - copied);
            if (span.IsEmpty)
                break;

            span.CopyTo(destination.Slice(copied));
            copied += span.Length;
        }

        return copied;
    }
}
