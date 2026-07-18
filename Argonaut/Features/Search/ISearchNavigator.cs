using System.Threading;
using System.Threading.Tasks;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Search;

/// <summary>
/// Display-side strategy for one open file: hands the search engine its scan target and
/// turns an engine result (a byte offset) into a visible, highlighted selection. This is
/// the seam that keeps FileSearchSession fully decoupled from the viewers.
/// </summary>
public interface ISearchNavigator
{
    /// <summary>The memory-mapped file the search engine should scan.</summary>
    MMapFile File { get; }

    /// <summary>Pushes the active find term into the view model(s) for row highlighting (null clears it).</summary>
    void SetHighlightTerm(string? term);

    /// <summary>
    /// Reveals the given match in the viewer - expanding/selecting/scrolling as needed.
    /// Called on the UI thread; may await index coverage or nested loads, honoring
    /// <paramref name="ct"/> (a newer find request cancels the reveal in flight).
    /// </summary>
    Task RevealAsync(SearchMatch match, CancellationToken ct);
}
