using System.Threading;
using System.Threading.Tasks;
using Argonaut.Engine.Search;
using Argonaut.Ui.Find;

namespace Argonaut.Ui.Tree;

/// <summary>
/// Reveal strategy for a whole-file tree view: the match's byte offset goes straight to the view
/// model, whose tree selects the row holding it and expands whatever hides it. The tree reads the
/// bytes directly, so there is no waiting for an index to cover the match first.
/// </summary>
public sealed class TreeSearchNavigator : ISearchNavigator
{
    private readonly TreeDocumentViewModel viewModel;

    public TreeSearchNavigator(TreeDocumentViewModel viewModel)
    {
        this.viewModel = viewModel;
    }

    public ScanTarget ScanTarget => viewModel.ScanTarget;

    public void SetHighlightTerm(string? term) => viewModel.HighlightTerm = term;

    public CancellationToken DocumentTearingDown => viewModel.TearingDown;

    public Task RevealAsync(SearchMatch match, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        viewModel.RevealMatch(match.Offset);
        return Task.CompletedTask;
    }
}
