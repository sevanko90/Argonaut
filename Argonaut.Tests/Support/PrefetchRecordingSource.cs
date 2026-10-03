using Argonaut.Engine.Bytes;

namespace Argonaut.Tests.Support;

/// <summary>
/// Bytes in memory that record every <see cref="IByteSource.Prefetch"/> hint they are given, so a
/// scan's read-ahead is observable without a cold disk to measure it against.
/// </summary>
internal sealed class PrefetchRecordingSource(byte[] all) : IByteSource
{
    public List<(long Offset, long Length)> Hints { get; } = new();

    public long AvailableLength => all.Length;

    public ReadOnlySpan<byte> GetContiguousSpan(long offset, int maxLength)
        => offset < 0 || offset >= all.Length || maxLength <= 0
            ? ReadOnlySpan<byte>.Empty
            : all.AsSpan((int)offset, (int)Math.Min(maxLength, all.Length - offset));

    public int CopyTo(long offset, Span<byte> destination)
    {
        var span = GetContiguousSpan(offset, destination.Length);
        span.CopyTo(destination);
        return span.Length;
    }

    public void Prefetch(long offset, long length) => Hints.Add((offset, length));
}
