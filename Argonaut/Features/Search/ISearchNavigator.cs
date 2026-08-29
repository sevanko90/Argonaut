using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Argonaut.Features.Search;

/// <summary>
/// Display-side strategy for one open document: hands the search engine its scan target(s) and
/// turns an engine result (a byte offset) into a visible, highlighted selection. This is
/// the seam that keeps FileSearchSession fully decoupled from the viewers - targets in,
/// reveals out. Note it hands over a <see cref="ScanTarget"/> (path, and range for a
/// sub-document), never a mapping: the engine opens what it reads, so nothing here couples
/// the document's mapping lifetime to a scan's.
///
/// Most viewers show one file and implement only the single-file members. The diff shows two,
/// and overrides <see cref="ScanTargets"/>, the indexed <see cref="RevealAsync(int, SearchMatch,
/// CancellationToken)"/>, and <see cref="OrderKey"/> so one find bar steps through both
/// documents as a single merged sequence.
/// </summary>
public interface ISearchNavigator
{
    /// <summary>What to scan - a file, or one byte range of one. NOT what is being searched
    /// for: the term reaches the engine as an <see cref="ISearchMatcher"/>, and this interface
    /// sees it only via <see cref="SetHighlightTerm"/>. For a multi-target navigator this is the
    /// first of <see cref="ScanTargets"/>.</summary>
    ScanTarget ScanTarget { get; }

    /// <summary>Pushes the active find term into the view model(s) for row highlighting (null clears it).</summary>
    void SetHighlightTerm(string? term);

    /// <summary>
    /// Reveals the given match in the viewer - expanding/selecting/scrolling as needed.
    /// Called on the UI thread; may await index coverage or nested loads, honoring
    /// <paramref name="ct"/> (a newer find request cancels the reveal in flight).
    /// </summary>
    Task RevealAsync(SearchMatch match, CancellationToken ct);

    /// <summary>
    /// Every target this navigator searches, in no particular order - <see cref="OrderKey"/>,
    /// not this list's order, decides how find steps through them. One session is started per
    /// entry. Single-file viewers inherit the default.
    /// </summary>
    IReadOnlyList<ScanTarget> ScanTargets => new[] { ScanTarget };

    /// <summary>Reveals a match found in <c>ScanTargets[<paramref name="fileIndex"/>]</c>.</summary>
    Task RevealAsync(int fileIndex, SearchMatch match, CancellationToken ct) => RevealAsync(match, ct);

    /// <summary>
    /// Fires when the owning document begins tearing down, so a REVEAL in flight is cancelled
    /// instead of reaching into a released mapping. Reveal is the only part of find that
    /// touches document-owned state: the scans read their own mappings and are indifferent to
    /// the document's lifetime.
    ///
    /// DELIBERATELY HAS NO DEFAULT IMPLEMENTATION, unlike every other optional member on this
    /// interface - do not add one. The others (<see cref="ScanTargets"/>, the indexed
    /// <see cref="RevealAsync(int, SearchMatch, CancellationToken)"/>, <see cref="OrderKey"/>,
    /// <see cref="StopUnit"/>) default to the CORRECT single-file behaviour, so a navigator that
    /// ignores them is right. The only available default here would be <c>default</c> - a token
    /// that never fires - which is not a weaker version of the right answer but the bug this
    /// member exists to prevent: a reveal awaiting index coverage would carry on into a released
    /// mapping, and it would fail as a hard crash at window-close time, in the one path the
    /// shell does not drive. A silent wrong default is worse than a compiler error, so
    /// implementers are made to say what fires it. A navigator with no session writes
    /// <c>=&gt; default</c> explicitly, which is greppable; an omission is not.
    /// </summary>
    CancellationToken DocumentTearingDown { get; }

    /// <summary>
    /// Sort key placing a match in the single merged order find steps through, or null for a
    /// match that is not a navigable stop at all. Byte offset is exactly right for one file; a
    /// multi-file navigator returns a key in ITS displayed order instead, so next/previous
    /// reads down the screen rather than exhausting one file before starting the next.
    ///
    /// Null is for a match the viewer cannot show. The diff renders some regions from one
    /// document into both panes, so bytes matched in the other one are on nobody's screen -
    /// stopping there would highlight nothing and, since the row it falls back to sits above
    /// the one just visited, would read as find-next jumping backwards. Skipping such matches
    /// is not lossy: what they matched is not displayed anywhere.
    ///
    /// Called on the UI thread, synchronously, a few times per find step - it must not block,
    /// and should stay stable as the user expands and collapses rows (a key derived from
    /// visible row positions would not).
    /// </summary>
    long? OrderKey(int fileIndex, SearchMatch match) => match.Offset;

    /// <summary>
    /// What one stop counts, when that is not simply one occurrence of the term. Null - the
    /// default - means the two are the same and the status needs no qualifier. The diff returns
    /// "rows" because several occurrences can share a row (a property name and its value; the
    /// same row's two panes) and it stops on the row once, so its count would otherwise look
    /// like it had lost matches the user can plainly see highlighted.
    /// </summary>
    string? StopUnit => null;
}
