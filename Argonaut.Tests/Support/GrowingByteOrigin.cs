using Argonaut.Engine.Bytes;

namespace Argonaut.Tests.Support;

/// <summary>
/// A document whose bytes arrive as <see cref="GrowingByteSource"/> releases them - so a test can
/// hold a document's indexing open for as long as it needs: a scan reads what has arrived, then
/// waits in <see cref="IByteSource.WaitForLength"/> until the source is sealed or the document is
/// torn down.
/// </summary>
internal sealed class GrowingByteOrigin(GrowingByteSource bytes, string displayName = "growing") : IByteOrigin
{
    private readonly MemoryByteOrigin kept = new([], displayName);

    public string DisplayName => displayName;
    public string? Path => null;
    public long AvailableLength => bytes.AvailableLength;
    public bool LengthSettled => bytes.LengthSettled;
    public KeptIndexes KeptIndexes => this.kept.KeptIndexes;
    public void WaitForLength(long atLeast, CancellationToken cancellationToken) => bytes.WaitForLength(atLeast, cancellationToken);
    public IByteSource Open() => bytes;
    public IByteSource OpenRange(long offset, long length) => throw new NotSupportedException();
    public void Dispose() => this.kept.Dispose();
}
