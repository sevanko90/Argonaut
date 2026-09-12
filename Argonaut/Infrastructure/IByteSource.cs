using System;

namespace Argonaut.Infrastructure;

/// <summary>
/// Random-access bytes that may not be physically contiguous. The abstraction exists so the raw
/// viewer can read from either the file mapping or, once editing lands, a piece table over
/// (mapping, scratch) without every reader learning which it has.
///
/// <see cref="MMapFile"/> is the single-buffer case: one mapping, so every request is served
/// whole and the zero-copy span behaviour is exactly what it always was. A piece table is the
/// general case, where a request spanning a piece boundary can only be served up to that
/// boundary - hence <see cref="GetContiguousSpan"/>'s short-return contract, and
/// <see cref="CopyTo"/> for callers that need a range in one buffer regardless.
/// </summary>
public interface IByteSource
{
    /// <summary>Total readable length. Never derived from a mapping's capacity (see CLAUDE.md).</summary>
    long Length { get; }

    /// <summary>
    /// Zero-copy view of up to <paramref name="maxLength"/> bytes from <paramref name="offset"/>,
    /// <b>truncated at the first internal boundary</b> - so a caller wanting a whole range must
    /// either loop until it has consumed what it asked for, or use <see cref="CopyTo"/>. Returns
    /// an empty span at or past <see cref="Length"/>, which is also the loop's termination
    /// signal; it never throws for an out-of-range read the way
    /// <see cref="ByteSourceReading.RequireContiguous"/> does, because a scan walking to EOF is
    /// the normal case here rather than a bug.
    /// </summary>
    ReadOnlySpan<byte> GetContiguousSpan(long offset, int maxLength);

    /// <summary>
    /// Copies up to <paramref name="destination"/>.Length bytes from <paramref name="offset"/>,
    /// crossing internal boundaries as needed. Returns the number copied, which is short only at
    /// end of data.
    /// </summary>
    int CopyTo(long offset, Span<byte> destination);
}
