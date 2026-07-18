using System.Threading;
using System.Threading.Tasks;
using Argonaut.Features.Json;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Search;

/// <summary>
/// Reveal strategy for a whole-file JSON view: resolve the match's byte offset to a token
/// (waiting for index coverage if the byte scan outran the indexer), then reuse the
/// breadcrumb-navigation path - SelectToken expands collapsed ancestors and the view syncs
/// selection/scroll from SelectedTokenIndex.
/// </summary>
public sealed class JsonSearchNavigator : ISearchNavigator
{
    private readonly JsonViewModel viewModel;

    public JsonSearchNavigator(JsonViewModel viewModel)
    {
        this.viewModel = viewModel;
    }

    public MMapFile File => viewModel.Mmap!;

    public void SetHighlightTerm(string? term) => viewModel.HighlightTerm = term;

    public async Task RevealAsync(SearchMatch match, CancellationToken ct)
    {
        var tokenIndex = await JsonOffsetTokenResolver.ResolveWhenCoveredAsync(viewModel.Index!, match.Offset, ct);
        ct.ThrowIfCancellationRequested();

        if (tokenIndex is int t)
            viewModel.SelectToken(t);
    }
}
