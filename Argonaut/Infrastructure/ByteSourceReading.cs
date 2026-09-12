using System;
using System.Text;

namespace Argonaut.Infrastructure;

/// <summary>
/// The two read idioms every <see cref="IByteSource"/> consumer needs on top of the interface's
/// own three members. They exist because <see cref="IByteSource.GetContiguousSpan"/> is
/// deliberately allowed to return less than was asked for: a span is a pointer and a length, so
/// it can only ever describe one contiguous run of memory, and a piece table's logical range may
/// live in two buffers.
///
/// Both yield exactly the range asked for or throw, which is the contract the old
/// <c>MMapFile.GetSpan</c> had - an out-of-range read stays a diagnosable managed exception
/// rather than becoming a silently short span (see CLAUDE.md on trailing-padding reads).
///
/// There is deliberately no "gather a split range into a pooled buffer" idiom here. The only
/// source that can split a range is <c>RawPieceTable</c>, the raw viewer's editing buffer, and
/// the raw viewer already gathers inline at the four places it needs to (see
/// <c>RawRowReader.ReadRow</c>) because only it knows each range's display cap. Every other
/// view - the JSON tree, the array table, NDJSON, CSV, type detection, search - reads from a
/// single-buffer source (a mapping, a clipboard array, a download spilled to a temp file) and is
/// not editable, so <see cref="RequireContiguous"/> is both correct and free there. If editing
/// ever reaches those views, its call sites are the worklist.
/// </summary>
public static class ByteSourceReading
{
    /// <summary>
    /// The single byte at <paramref name="offset"/>. Always servable whole - one byte cannot
    /// straddle anything - so this is the peek idiom for scanners walking backwards or probing a
    /// known position.
    /// </summary>
    public static byte ByteAt(this IByteSource source, long offset)
    {
        var span = source.GetContiguousSpan(offset, 1);
        if (span.IsEmpty)
            throw new ArgumentOutOfRangeException(nameof(offset),
                $"Offset {offset} is outside the readable range (0 to {source.AvailableLength}).");

        return span[0];
    }

    /// <summary>
    /// The whole of [<paramref name="offset"/>, <paramref name="offset"/> + <paramref name="length"/>)
    /// as one zero-copy span, or throws. Never copies, at any size - so it is as cheap as the
    /// mapping read it replaced, whatever the range's size.
    ///
    /// Throws <see cref="ArgumentOutOfRangeException"/> when the range runs past the end of the
    /// data, and <see cref="NotSupportedException"/> when the data is there but split across the
    /// source's internal buffers. See this class's remarks for why the second case is unreachable
    /// from every caller that uses this.
    /// </summary>
    public static ReadOnlySpan<byte> RequireContiguous(this IByteSource source, long offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length == 0)
            return ReadOnlySpan<byte>.Empty;

        var contiguous = source.GetContiguousSpan(offset, length);
        if (contiguous.Length == length)
            return contiguous;

        if (offset + length > source.AvailableLength)
            throw new ArgumentOutOfRangeException(nameof(length),
                $"Requested range [{offset}, {offset + length}) extends past the end of the data " +
                $"({source.AvailableLength} bytes).");

        throw new NotSupportedException(
            $"Range [{offset}, {offset + length}) is split across this source's internal buffers " +
            $"(only {contiguous.Length} bytes are contiguous). This caller reads whole ranges of " +
            "unbounded size, so it needs a display cap or a streaming decode rather than a copy.");
    }

    /// <summary>
    /// Decodes [<paramref name="offset"/>, <paramref name="offset"/> + <paramref name="length"/>)
    /// as UTF-8. The one place the "decode text on demand from an (offset, length) range" idiom
    /// lives, so every reader goes through one bounds check (see CLAUDE.md).
    /// </summary>
    public static string GetUtf8String(this IByteSource source, long offset, int length)
        => Encoding.UTF8.GetString(source.RequireContiguous(offset, length));

    /// <summary>
    /// Releases whatever the source holds open, for the one owner of it - the document session.
    /// Not every source holds anything: a mapping must be unmapped, an in-memory payload has
    /// nothing to release and does not implement <see cref="IDisposable"/> at all. So this is a
    /// no-op rather than a constraint on the interface, which keeps the release decision in the
    /// same one place as the teardown ordering that makes it safe (see
    /// <see cref="IndexedSourceSession{TIndex}"/>).
    ///
    /// Only a session that owns its source may call this. Sub-range readers and search hold
    /// their own sources and release those; nobody releases a source handed to them.
    /// </summary>
    public static void Release(this IByteSource source) => (source as IDisposable)?.Dispose();
}
