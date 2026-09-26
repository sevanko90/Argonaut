namespace Argonaut.Engine.Bytes;

/// <summary>
/// The bytes an index is built over: an origin, at the version it had when the view opened it.
/// The one place a view asks the origin for an index kept by an earlier view (see
/// <see cref="KeptIndexes"/>) and hands back its own once finished - both only while the origin
/// is still at that version, so a file edited on disk or replaced by a save while the view was
/// open never gets an index for bytes it no longer holds.
/// </summary>
public sealed class IndexBasis
{
    private readonly IByteOrigin origin;
    private readonly ByteOriginVersion version;

    private IndexBasis(IByteOrigin origin, ByteOriginVersion version)
    {
        this.origin = origin;
        this.version = version;
    }

    /// <summary>The basis for an index of <paramref name="origin"/> as it is now, or null while its
    /// bytes are still arriving - nothing is kept for a document that is still growing.</summary>
    public static IndexBasis? Of(IByteOrigin origin)
        => ByteOriginVersion.Of(origin) is { } version ? new IndexBasis(origin, version) : null;

    /// <summary>The index an earlier view kept under <paramref name="key"/> for these bytes, or
    /// null.</summary>
    public TIndex? FindKept<TIndex>(object key) where TIndex : class
        => IsCurrent && this.origin.KeptIndexes.TryGet<TIndex>(key, this.version, out var kept) ? kept : null;

    /// <summary>Keeps <paramref name="index"/> - a finished index detached from its source - for the
    /// next view of these bytes. Nothing is kept when there is nothing to keep, or when the origin
    /// has changed since the index was started.</summary>
    public void Keep(object key, object? index)
    {
        if (index is not null && IsCurrent)
            this.origin.KeptIndexes.Keep(key, this.version, index);
    }

    private bool IsCurrent => ByteOriginVersion.Of(this.origin) == this.version;
}
