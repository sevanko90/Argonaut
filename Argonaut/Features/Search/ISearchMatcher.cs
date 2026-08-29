using System;

namespace Argonaut.Features.Search;

/// <summary>
/// Strategy for locating matches inside one chunk of raw file bytes. Implementations are
/// stateless with respect to file position - <see cref="FileSearchSession"/> owns the chunked
/// walk over the file and hands each chunk here. This is the extension point for future
/// match kinds (e.g. regex, or a decoded-text matcher that understands JSON string escapes).
/// </summary>
public interface ISearchMatcher
{
    /// <summary>
    /// How many bytes of context must be re-scanned across chunk boundaries so a match
    /// straddling two chunks is still seen whole (needle length - 1 for a literal; a regex
    /// matcher would declare its own bound).
    /// </summary>
    int ChunkOverlap { get; }

    /// <summary>
    /// Finds the first complete match starting at or after <paramref name="from"/> within
    /// <paramref name="chunk"/>. Returns false when the rest of the chunk holds no match.
    /// </summary>
    bool TryFindNext(ReadOnlySpan<byte> chunk, int from, out int matchIndex, out int matchLength);
}
