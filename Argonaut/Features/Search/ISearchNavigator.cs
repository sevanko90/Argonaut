using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Search;

/// <summary>
/// Display-side strategy for one open document: hands the search engine its scan target(s) and
/// turns an engine result (a byte offset) into a visible, highlighted selection. This is
/// the seam that keeps FileSearchSession fully decoupled from the viewers.
///
/// Most viewers show one file and implement only the single-file members. The diff shows two,
/// and overrides <see cref="Files"/>, the indexed <see cref="RevealAsync(int, SearchMatch,
/// CancellationToken)"/>, and <see cref="OrderKey"/> so one find bar steps through both
/// documents as a single merged sequence.
/// </summary>
public interface ISearchNavigator
{
    /// <summary>The memory-mapped file the search engine should scan. For a multi-file
    /// navigator this is the first of <see cref="Files"/>.</summary>
    MMapFile File { get; }

    /// <summary>Pushes the active find term into the view model(s) for row highlighting (null clears it).</summary>
    void SetHighlightTerm(string? term);

    /// <summary>
    /// Reveals the given match in the viewer - expanding/selecting/scrolling as needed.
    /// Called on the UI thread; may await index coverage or nested loads, honoring
    /// <paramref name="ct"/> (a newer find request cancels the reveal in flight).
    /// </summary>
    Task RevealAsync(SearchMatch match, CancellationToken ct);

    /// <summary>
    /// Every file this navigator searches, in no particular order - <see cref="OrderKey"/>,
    /// not this list's order, decides how find steps through them. One session is started per
    /// entry. Single-file viewers inherit the default.
    /// </summary>
    IReadOnlyList<MMapFile> Files => new[] { File };

    /// <summary>Reveals a match found in <c>Files[<paramref name="fileIndex"/>]</c>.</summary>
    Task RevealAsync(int fileIndex, SearchMatch match, CancellationToken ct) => RevealAsync(match, ct);

    /// <summary>
    /// Sort key placing a match in the single merged order find steps through. Byte offset is
    /// exactly right for one file; a multi-file navigator returns a key in ITS displayed order
    /// instead, so next/previous reads down the screen rather than exhausting one file before
    /// starting the next.
    ///
    /// Called on the UI thread, synchronously, a couple of times per find step - it must not
    /// block, and should stay stable as the user expands and collapses rows (a key derived
    /// from visible row positions would not).
    /// </summary>
    long OrderKey(int fileIndex, SearchMatch match) => match.Offset;
}
