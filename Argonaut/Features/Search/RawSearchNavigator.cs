using System.Threading;
using System.Threading.Tasks;
using Argonaut.Features.Raw;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Search;

/// <summary>
/// Single-stage reveal strategy for the raw viewer (like CsvSearchNavigator - no nested view
/// model to wait on): resolve the match's byte offset to a display row via
/// RawOffsetRowResolver and hand it to the view model for the view to select/scroll.
///
/// One raw-specific twist: a wrap-width change replaces the index mid-flight. The resolver may
/// then have resolved against the retired index - which was cancel-completed, so its answer can
/// be stale or partial. The generation re-check catches that and resolves once more against the
/// current index; the file itself never changes, so the match offset stays valid throughout.
/// </summary>
public sealed class RawSearchNavigator : ISearchNavigator
{
    private readonly RawViewModel viewModel;

    public RawSearchNavigator(RawViewModel viewModel)
    {
        this.viewModel = viewModel;
    }

    public MMapFile File => viewModel.Mmap!;

    public void SetHighlightTerm(string? term) => viewModel.HighlightTerm = term;

    public async Task RevealAsync(SearchMatch match, CancellationToken ct)
    {
        int generation = viewModel.IndexGeneration;
        var row = await RawOffsetRowResolver.ResolveWhenCoveredAsync(viewModel.Index!, match.Offset, ct);
        ct.ThrowIfCancellationRequested();

        if (generation != viewModel.IndexGeneration)
        {
            row = await RawOffsetRowResolver.ResolveWhenCoveredAsync(viewModel.Index!, match.Offset, ct);
            ct.ThrowIfCancellationRequested();
        }

        if (row is int rowIndex)
            viewModel.SelectRow(rowIndex);
    }
}
