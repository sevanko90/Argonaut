using System;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Search;

/// <summary>
/// UI-side orchestration of find / find next for the currently open file. Owns the search
/// session lifecycle (one at a time; a changed term or a stop cancels the previous scan),
/// the result cursor and wrap semantics, and hands each chosen match to the attached
/// <see cref="ISearchNavigator"/> to reveal.
///
/// All public members run on the UI thread; awaits resume there, and a monotonic request id
/// (the codebase's staleness idiom) guards every post-await continuation against a newer
/// request having taken over.
/// </summary>
public sealed class FindController
{
    private readonly Action<string?> statusChanged;
    private readonly Func<IProgressReporter?> progressReporterFactory;

    private ISearchNavigator? navigator;
    private FileSearchSession? session;
    private string? sessionTerm;
    private int cursor = -1;
    private long requestId;
    private CancellationTokenSource? revealCts;

    public FindController(Action<string?> statusChanged, Func<IProgressReporter?> progressReporterFactory)
    {
        this.statusChanged = statusChanged;
        this.progressReporterFactory = progressReporterFactory;
    }

    /// <summary>Attaches the navigator for a newly opened file. Call after <see cref="StopAsync"/>.</summary>
    public void Attach(ISearchNavigator navigator)
    {
        this.navigator = navigator;
    }

    /// <summary>
    /// Finds the next (<paramref name="direction"/> &gt;= 0) or previous match of
    /// <paramref name="term"/>, starting a fresh background scan when the term changed.
    /// </summary>
    public async Task FindAsync(string term, int direction)
    {
        if (navigator is null || string.IsNullOrEmpty(term))
            return;

        long request = ++requestId;
        CancelReveal();

        if (session is null || !string.Equals(term, sessionTerm, StringComparison.Ordinal))
        {
            await DisposeSessionAsync();
            if (request != requestId)
                return;

            sessionTerm = term;
            cursor = -1;
            session = FileSearchSession.Start(navigator.File, new LiteralSearchMatcher(term), progressReporterFactory());
            navigator.SetHighlightTerm(term);
            _ = RefreshStatusOnCompletionAsync(session, request);
        }

        var activeSession = session;
        bool wrapped = false;

        if (direction >= 0)
        {
            // Wait for the next match to stream in; never wrap while the scan is still
            // running, so "n of m" stays monotone.
            while (activeSession.MatchCount <= cursor + 1 && !activeSession.IsComplete)
            {
                statusChanged("Searching…");
                await activeSession.WaitForMatchCountAsync(cursor + 2);
                if (request != requestId)
                    return;
            }

            if (activeSession.MatchCount == 0)
            {
                UpdateStatus(activeSession, wrapped: false);
                return;
            }

            if (cursor + 1 < activeSession.MatchCount)
            {
                cursor += 1;
            }
            else
            {
                cursor = 0;
                wrapped = true;
            }
        }
        else
        {
            if (activeSession.MatchCount == 0)
            {
                UpdateStatus(activeSession, wrapped: false);
                return;
            }

            if (cursor > 0)
            {
                cursor -= 1;
            }
            else if (activeSession.IsComplete)
            {
                // Wrapping backward needs the full match list, so it too waits for completion.
                cursor = activeSession.MatchCount - 1;
                wrapped = true;
            }
            else
            {
                UpdateStatus(activeSession, wrapped: false);
                return;
            }
        }

        UpdateStatus(activeSession, wrapped);

        var cts = new CancellationTokenSource();
        revealCts = cts;
        try
        {
            await navigator.RevealAsync(activeSession.GetMatch(cursor), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // A newer request or a stop superseded this reveal.
        }
    }

    /// <summary>
    /// Stops the active search: cancels any in-flight reveal, cancels the background scan
    /// and waits for it to let go of the file, and clears row highlighting. MUST complete
    /// before the current view's MMapFile is disposed - a scan thread touching a disposed
    /// mapping is an access violation.
    /// </summary>
    public async Task StopAsync()
    {
        ++requestId;
        CancelReveal();
        navigator?.SetHighlightTerm(null);
        statusChanged(null);
        await DisposeSessionAsync();
    }

    /// <summary>Stops the active search and forgets the current file's navigator.</summary>
    public async Task DetachAsync()
    {
        await StopAsync();
        navigator = null;
    }

    private void CancelReveal()
    {
        revealCts?.Cancel();
        revealCts = null;
    }

    private async Task DisposeSessionAsync()
    {
        var old = session;
        session = null;
        sessionTerm = null;
        cursor = -1;

        if (old is null)
            return;

        old.Cancel();
        try
        {
            await old.ScanTask;
        }
        catch
        {
            // A failed scan has nothing further to release; surfacing it here would only
            // break the stop path.
        }
    }

    /// <summary>
    /// Refreshes the "n of m (searching…)" status once the scan finishes, so the count
    /// stops advertising an in-progress search that already ended.
    /// </summary>
    private async Task RefreshStatusOnCompletionAsync(FileSearchSession trackedSession, long request)
    {
        try
        {
            await trackedSession.ScanTask;
        }
        catch
        {
            return;
        }

        if (request != requestId || trackedSession.WasCancelled)
            return;

        UpdateStatus(trackedSession, wrapped: false);
    }

    private void UpdateStatus(FileSearchSession activeSession, bool wrapped)
    {
        string text;
        if (activeSession.MatchCount == 0)
        {
            text = activeSession.IsComplete ? "No matches" : "Searching…";
        }
        else
        {
            text = cursor >= 0
                ? $"{cursor + 1:N0} of {activeSession.MatchCount:N0}"
                : $"{activeSession.MatchCount:N0} matches";

            if (!activeSession.IsComplete)
                text += " (searching…)";
            else if (activeSession.HitMatchCap)
                text += $" (first {activeSession.MatchCount:N0} only)";

            if (wrapped)
                text += " — wrapped";
        }

        statusChanged(text);
    }
}
