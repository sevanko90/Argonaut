using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Diff;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Search;

/// <summary>
/// Search strategy for the diff: two scanned files (source and target) feeding one find bar.
/// Both documents are scanned independently, and <see cref="OrderKey"/> interleaves their
/// matches into the merged row order the diff already displays - so find next/previous reads
/// down the single list rather than draining one document before starting the other.
///
/// Revealing keeps the panes in step for free: the diff is one merged list whose rows carry
/// both sides, so selecting the row that holds a match shows its counterpart alongside it,
/// whichever document the match came from.
/// </summary>
public sealed class JsonDiffSearchNavigator : ISearchNavigator
{
    private readonly JsonDiffViewModel viewModel;
    private readonly JsonDiffSession session;
    private readonly ScanTarget[] scanTargets;

    /// <summary>
    /// Takes the diff's session rather than the two paths, so the scan targets and
    /// <see cref="DocumentTearingDown"/> come from one source and cannot drift apart.
    /// </summary>
    public JsonDiffSearchNavigator(JsonDiffViewModel viewModel, JsonDiffSession session)
    {
        this.viewModel = viewModel;
        this.session = session;
        this.scanTargets = new[] { new ScanTarget(session.LeftPath), new ScanTarget(session.RightPath) };
    }

    /// <summary>Index into <see cref="ScanTargets"/> of the left (source) document.</summary>
    private const int LeftFile = 0;

    public ScanTarget ScanTarget => scanTargets[LeftFile];

    public IReadOnlyList<ScanTarget> ScanTargets => scanTargets;

    public void SetHighlightTerm(string? term) => viewModel.HighlightTerm = term;

    /// <summary>Both sides at once: the diff is torn down as a whole, and its own source is
    /// linked over both sides' - see <see cref="JsonDiffSession.TearingDown"/>.</summary>
    public CancellationToken DocumentTearingDown => session.TearingDown;

    public Task RevealAsync(SearchMatch match, CancellationToken ct) => RevealAsync(LeftFile, match, ct);

    public Task RevealAsync(int fileIndex, SearchMatch match, CancellationToken ct)
        => viewModel.RevealMatchAsync(leftSide: fileIndex == LeftFile, match, ct);

    public long? OrderKey(int fileIndex, SearchMatch match)
        => viewModel.MatchOrderKey(leftSide: fileIndex == LeftFile, match);

    /// <summary>Stops are rows, not raw occurrences - matches sharing a row collapse into one.
    /// Deliberately visible in the status: an unchanged object whose members were reordered
    /// renders from the left document into both panes, so the right file's copies highlight but
    /// are the same rows, and a count of occurrences would not add up to what is on screen.</summary>
    public string? StopUnit => "rows";
}
