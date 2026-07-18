using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Features.Json;
using Argonaut.Features.NdJson;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Search;

/// <summary>
/// Two-stage reveal strategy for the NDJSON view: resolve the match's byte offset to a line
/// and select it (which kicks off the nested per-line JsonViewModel load), then - once that
/// nested view model is published - translate the absolute offset into the line's sub-range
/// mapping and reveal the token inside the nested JSON tree.
///
/// Runs on the UI thread throughout (awaits resume on the dispatcher), so it serializes
/// naturally with user-driven LoadSelectedLine calls; after any await it re-checks that the
/// user hasn't selected a different line, in which case the reveal quietly yields.
/// </summary>
public sealed class NdJsonSearchNavigator : ISearchNavigator
{
    private readonly NdJsonViewModel viewModel;

    public NdJsonSearchNavigator(NdJsonViewModel viewModel)
    {
        this.viewModel = viewModel;
    }

    public MMapFile File => viewModel.Mmap!;

    public void SetHighlightTerm(string? term) => viewModel.HighlightTerm = term;

    public async Task RevealAsync(SearchMatch match, CancellationToken ct)
    {
        var line = await NdJsonOffsetLineResolver.ResolveWhenCoveredAsync(viewModel.Index!, match.Offset, ct);
        ct.ThrowIfCancellationRequested();
        if (line is not int lineIndex)
            return;

        if (viewModel.SelectedLineNumber != lineIndex + 1 || viewModel.SelectedLineJsonViewModel is null)
        {
            viewModel.LoadSelectedLine(lineIndex);
            await WaitForNestedViewModelAsync(ct);

            // The user may have clicked another line while the nested load ran - their
            // intent wins, so leave whatever they selected alone.
            if (viewModel.SelectedLineNumber != lineIndex + 1)
                return;
        }

        var nested = viewModel.SelectedLineJsonViewModel;
        if (nested is null)
            return;

        // The nested MMapFile is zero-based at the trimmed line start; TrimTrailingNewline
        // never moves the start (it only shortens the end), so the line span's offset is the
        // sub-range origin.
        var lineSpan = viewModel.Index!.GetLineSpan(lineIndex);
        long relativeOffset = match.Offset - lineSpan.Offset;
        if (relativeOffset < 0 || relativeOffset >= nested.Mmap!.Length)
            return; // hit landed on the line's trailing newline bytes - the line selection is enough

        var tokenIndex = await JsonOffsetTokenResolver.ResolveWhenCoveredAsync(nested.Index!, relativeOffset, ct);
        ct.ThrowIfCancellationRequested();

        // Re-check once more: the nested VM is disposed if the selection moved on.
        if (viewModel.SelectedLineJsonViewModel != nested)
            return;

        if (tokenIndex is int t)
            nested.SelectToken(t);
    }

    /// <summary>
    /// Waits for SelectedLineJsonViewModel to be published after a LoadSelectedLine call.
    /// If the line's JSON fails to parse the property never fires; the wait then ends only
    /// via <paramref name="ct"/> (the next find/close cancels it), which is acceptable.
    /// </summary>
    private async Task WaitForNestedViewModelAsync(CancellationToken ct)
    {
        if (viewModel.SelectedLineJsonViewModel is not null)
            return;

        var published = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(NdJsonViewModel.SelectedLineJsonViewModel) &&
                viewModel.SelectedLineJsonViewModel is not null)
            {
                published.TrySetResult(true);
            }
        }

        viewModel.PropertyChanged += OnPropertyChanged;
        using var registration = ct.Register(() => published.TrySetCanceled(ct));
        try
        {
            await published.Task;
        }
        finally
        {
            viewModel.PropertyChanged -= OnPropertyChanged;
        }
    }
}
