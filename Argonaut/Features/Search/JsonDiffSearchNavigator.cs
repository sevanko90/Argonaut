using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly MMapFile[] files;

    public JsonDiffSearchNavigator(JsonDiffViewModel viewModel, MMapFile left, MMapFile right)
    {
        this.viewModel = viewModel;
        this.files = new[] { left, right };
    }

    /// <summary>Index into <see cref="Files"/> of the left (source) document.</summary>
    private const int LeftFile = 0;

    public MMapFile File => files[LeftFile];

    public IReadOnlyList<MMapFile> Files => files;

    public void SetHighlightTerm(string? term) => viewModel.HighlightTerm = term;

    public Task RevealAsync(SearchMatch match, CancellationToken ct) => RevealAsync(LeftFile, match, ct);

    public Task RevealAsync(int fileIndex, SearchMatch match, CancellationToken ct)
        => viewModel.RevealMatchAsync(leftSide: fileIndex == LeftFile, match, ct);

    public long? OrderKey(int fileIndex, SearchMatch match)
        => viewModel.MatchOrderKey(leftSide: fileIndex == LeftFile, match);
}
