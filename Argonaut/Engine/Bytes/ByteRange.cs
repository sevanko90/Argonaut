namespace Argonaut.Engine.Bytes;

/// <summary>
/// A run of bytes in a document: [<paramref name="Offset"/>, <paramref name="Offset"/> +
/// <paramref name="Length"/>). A zero length is a position rather than a span - a caret, or a
/// node whose end is not known yet.
/// </summary>
public readonly record struct ByteRange(long Offset, long Length)
{
    public long End => Offset + Length;

    public static ByteRange At(long offset) => new(offset, 0);
}
