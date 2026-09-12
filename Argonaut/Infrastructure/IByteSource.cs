using System;
using System.Threading;

namespace Argonaut.Infrastructure;

/// <summary>
/// Random-access bytes that may not be physically contiguous, and may still be arriving. The
/// abstraction exists so a reader does not learn where its bytes came from: the raw viewer reads
/// from either the file mapping or a piece table over (mapping, scratch), and a document can be
/// opened from a path, a clipboard payload or a streamed download.
///
/// <see cref="MMapFile"/> is the simple case on both axes: one mapping, fully present, so every
/// request is served whole and <see cref="LengthSettled"/> is true from birth. A piece table is
/// the non-contiguous case, where a request spanning a piece boundary can only be served up to
/// that boundary - hence <see cref="GetContiguousSpan"/>'s short-return contract, and
/// <see cref="CopyTo"/> for callers that need a range in one buffer regardless. A streamed source
/// is the still-arriving case - see <see cref="AvailableLength"/>.
/// </summary>
public interface IByteSource
{
    /// <summary>
    /// How much is readable <b>right now</b>. Named for that rather than "Length" because a
    /// scanner that snapshots it once and loops to it will silently stop at whatever had arrived
    /// when it started, and then report a complete index over a partial document. A scan reads to
    /// <see cref="AvailableLength"/>, and when it gets there asks <see cref="LengthSettled"/>
    /// whether that was the end or merely the end so far.
    ///
    /// Never derived from a mapping's capacity (see CLAUDE.md).
    /// </summary>
    long AvailableLength { get; }

    /// <summary>
    /// True once <see cref="AvailableLength"/> will not grow again, so a scan that has reached it
    /// is genuinely finished. True from birth for every source whose bytes are all present when
    /// it is constructed - a mapping, an array, a piece table (a piece table's length changes
    /// through user edits, which is not bytes arriving, and an edit invalidates the index rather
    /// than extending it). Only a source still receiving data reports false.
    /// </summary>
    bool LengthSettled => true;

    /// <summary>
    /// Blocks until <see cref="AvailableLength"/> is at least <paramref name="atLeast"/> or
    /// <see cref="LengthSettled"/> becomes true, whichever happens first - so a caller loops on
    /// the pair rather than trusting this to deliver what it asked for.
    ///
    /// Blocking, not async, and deliberately: the callers are the background scan bodies
    /// (<c>FileOffsetIndex</c>, <c>JsonStructureIndex</c>, <c>RawSegmentIndex</c>,
    /// <c>FileSearchSession</c>), which already run inside a <see cref="System.Threading.Tasks.Task"/>
    /// body off the UI thread. Waiting there is legitimate and keeps those parse loops
    /// synchronous. Never call it from the UI thread.
    ///
    /// A no-op for an already-settled source, which is every source that exists today.
    /// </summary>
    void WaitForLength(long atLeast, CancellationToken cancellationToken) { }

    /// <summary>
    /// Zero-copy view of up to <paramref name="maxLength"/> bytes from <paramref name="offset"/>,
    /// <b>truncated at the first internal boundary</b> - so a caller wanting a whole range must
    /// either loop until it has consumed what it asked for, or use <see cref="CopyTo"/>. Returns
    /// an empty span at or past <see cref="AvailableLength"/>, which is also the loop's
    /// termination signal; it never throws for an out-of-range read the way
    /// <see cref="ByteSourceReading.RequireContiguous"/> does, because a scan walking to the end
    /// of what has arrived is the normal case here rather than a bug.
    /// </summary>
    ReadOnlySpan<byte> GetContiguousSpan(long offset, int maxLength);

    /// <summary>
    /// Copies up to <paramref name="destination"/>.Length bytes from <paramref name="offset"/>,
    /// crossing internal boundaries as needed. Returns the number copied, which is short only at
    /// the end of what is currently available.
    /// </summary>
    int CopyTo(long offset, Span<byte> destination);
}
