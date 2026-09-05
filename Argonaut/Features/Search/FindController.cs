using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Search;

/// <summary>
/// UI-side orchestration of find / find next for the currently open document: the scans'
/// lifetime (one <see cref="FileSearchSession"/> per target), waiting for more results, the
/// reveal, and the press queue. Where the stops are ordered and stepped is
/// <see cref="FindCursor"/>; the status line is <see cref="FindStatusText"/>.
///
/// Stopping a scan is a request, never a join: each scan owns its mappings, so a retired one
/// winding down holds nothing the document needs back. The only part of find tied to the
/// document's lifetime is the REVEAL, which links
/// <see cref="ISearchNavigator.DocumentTearingDown"/>.
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
    private FileSearchSession[] sessions = Array.Empty<FileSearchSession>();
    private string? sessionTerm;

    private readonly FindCursor cursor = new();

    private readonly RequestTicket findRequest = new();
    private CancellationTokenSource? revealCts;

    /// <summary>Depth of the press queue behind an in-flight find - see <see cref="FindAsync"/>.</summary>
    private const int MaxQueuedFinds = 8;

    private bool running;
    private readonly Queue<(string Term, int Direction)> queued = new();

    public FindController(Action<string?> statusChanged, Func<IProgressReporter?> progressReporterFactory)
    {
        this.statusChanged = statusChanged;
        this.progressReporterFactory = progressReporterFactory;
    }

    /// <summary>
    /// Attaches the navigator for a newly opened document, or null for one with nothing
    /// searchable. Call after <see cref="StopSearch"/>.
    /// </summary>
    public void Attach(ISearchNavigator? navigator)
    {
        this.navigator = navigator;
    }

    /// <summary>
    /// Finds the next (<paramref name="direction"/> &gt;= 0) or previous match of
    /// <paramref name="term"/>, starting fresh background scans when the term changed.
    ///
    /// Presses are SERIALIZED, not run concurrently. The shell fires these and forgets them
    /// (`_ = FindAsync(...)` on Enter), so holding the key starts one call per repeat, and each
    /// does synchronous work the UI thread cannot be preempted out of. Left overlapping, those
    /// pile up faster than they drain and the window stops painting. Queued presses still each
    /// advance one match; only a leaned-on key past <see cref="MaxQueuedFinds"/> is dropped,
    /// which is the case where the user cannot be tracking individual steps anyway.
    /// </summary>
    public async Task FindAsync(string term, int direction)
    {
        if (running)
        {
            if (queued.Count < MaxQueuedFinds)
                queued.Enqueue((term, direction));
            return;
        }

        running = true;
        try
        {
            await FindCoreAsync(term, direction);

            while (queued.Count > 0)
            {
                var (nextTerm, nextDirection) = queued.Dequeue();
                await FindCoreAsync(nextTerm, nextDirection);
            }
        }
        finally
        {
            running = false;
            queued.Clear();
        }
    }

    private async Task FindCoreAsync(string term, int direction)
    {
        if (navigator is null || string.IsNullOrEmpty(term))
            return;

        long request = findRequest.Begin();
        CancelReveal();

        if (sessions.Length == 0 || !string.Equals(term, sessionTerm, StringComparison.Ordinal))
        {
            // Synchronous - the retired scans own their mappings, so nothing needs joining.
            StopSessions();

            var scanTargets = navigator.ScanTargets;
            sessionTerm = term;
            sessions = new FileSearchSession[scanTargets.Count];
            cursor.Reset(scanTargets.Count);
            for (int i = 0; i < scanTargets.Count; i++)
            {
                sessions[i] = FileSearchSession.Start(scanTargets[i], new LiteralSearchMatcher(term),
                    progressReporterFactory());
            }

            navigator.SetHighlightTerm(term);
            _ = RefreshStatusOnCompletionAsync(sessions, request);
        }

        if (direction >= 0)
        {
            if (!await EnsureStopAfterPositionAsync(request))
                return;
        }
        else
        {
            // Going back, whatever has been found already is all there is to step onto.
            cursor.Fold(sessions, navigator.OrderKey);
        }

        // The move reports the position and count it settled on, so the status cannot disagree
        // with the stop chosen even if the completion refresh folds more in between.
        var move = cursor.Move(direction, AllComplete());
        ReportStatus(move.Position, move.Count, move.Wrapped);
        if (!move.Moved)
            return;

        // Linked to the document's teardown so a reveal in flight when it is torn down WITHOUT
        // going through Stop/Detach (the view's own detach handler on window close) is cancelled
        // rather than reaching into a released mapping. findRequest covers only a newer find
        // superseding this one - it says nothing about the document going away.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(navigator.DocumentTearingDown);
        revealCts = cts;
        try
        {
            var stop = move.Stop;
            await navigator.RevealAsync(stop.Source, sessions[stop.Source].GetMatch(stop.MatchIndex), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // A newer request, a stop, or the document tearing down superseded this reveal.
        }
        // cts is deliberately not disposed: a concurrent CancelReveal() may still hold it, and
        // an un-disposed CTS without timers costs nothing beyond GC.
    }

    /// <summary>
    /// Waits until there is a stop past the current position, or every scan has finished. False
    /// when a newer request took over mid-wait. Matches the viewer cannot show are folded away
    /// by the cursor, so a long run of them costs one pass, not one wait each.
    /// </summary>
    private async Task<bool> EnsureStopAfterPositionAsync(long request)
    {
        while (true)
        {
            // Read completion BEFORE folding, the same way IndexGrowthMonitor's tick does.
            // A scan publishes its matches and only then marks itself complete, so a fold taken
            // after "everything finished" is guaranteed to see all of them - while the other
            // order loses any match published in between, and this loop would exit on the
            // completion it just observed without ever folding that match in. The press then
            // moved onto an empty stop list and reported "No matches" over a match that was
            // there, for good: nothing folds again until the next press.
            bool complete = AllComplete();

            if (cursor.Fold(sessions, navigator!.OrderKey).HasStopAhead || complete)
                return true;

            statusChanged("Searching…");

            var waits = new List<Task>(sessions.Length);
            foreach (var session in sessions)
            {
                if (!session.IsComplete)
                    waits.Add(session.WaitForMatchCountAsync(session.MatchCount + 1));
            }

            // Everything finished while we were looking; round again to fold and report it.
            if (waits.Count == 0)
                continue;

            await Task.WhenAny(waits);
            if (!findRequest.IsCurrent(request))
                return false;
        }
    }

    /// <summary>True when every scan stopped because it could not read its target at all -
    /// the file was deleted, locked or replaced between opening the document and searching it.
    /// Reported as "Search failed" rather than "No matches", which would be a lie.</summary>
    private bool AllFailedToOpen()
    {
        if (sessions.Length == 0)
            return false;

        foreach (var session in sessions)
        {
            if (session.OpenFailure is null)
                return false;
        }

        return true;
    }

    private bool AllComplete()
    {
        foreach (var session in sessions)
        {
            if (!session.IsComplete)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Stops the active search: cancels any in-flight reveal, asks the background scans to
    /// stop, and clears row highlighting. Returns immediately - the scans may still be winding
    /// down, which is harmless now each owns its own mapping. Callers stop find before a
    /// content swap for UI reasons (clearing the highlight and the find-bar status), not to
    /// make the swap safe.
    /// </summary>
    public void StopSearch()
    {
        findRequest.Begin();
        CancelReveal();
        navigator?.SetHighlightTerm(null);
        statusChanged(null);
        StopSessions();
    }

    /// <summary>Stops the active search and forgets the current document's navigator.</summary>
    public void Detach()
    {
        StopSearch();
        navigator = null;
    }

    private void CancelReveal()
    {
        revealCts?.Cancel();
        revealCts = null;
    }

    /// <summary>
    /// Retires the current scans: clears the result state and asks each scan to stop. Nothing
    /// is joined - a retired scan holds only its own 4MB chunk mapping and lets go of it within
    /// one chunk's work, and FileSearchSession.ScanTask never faults, so there is no exception
    /// to observe either.
    /// </summary>
    private void StopSessions()
    {
        // UI thread only, like every other mutation of `sessions` - the one background toucher
        // (RefreshStatusOnCompletionAsync) reads the array it captured at start, never this field.
        var old = sessions;
        sessions = Array.Empty<FileSearchSession>();
        sessionTerm = null;
        cursor.Reset(0);

        foreach (var session in old)
            session.RequestStop();
    }

    /// <summary>
    /// Refreshes the "n of m (searching…)" status once every scan finishes, so the count stops
    /// advertising an in-progress search that already ended - and settles on the final stop
    /// count, which only the completed scans can give.
    /// </summary>
    private async Task RefreshStatusOnCompletionAsync(FileSearchSession[] tracked, long request)
    {
        foreach (var session in tracked)
        {
            try
            {
                await session.ScanTask;
            }
            catch
            {
                return;
            }
        }

        if (!findRequest.IsCurrent(request))
            return;

        foreach (var session in tracked)
        {
            if (session.WasCancelled)
                return;
        }

        if (navigator is not { } current)
            return;

        var state = cursor.Fold(tracked, current.OrderKey);
        ReportStatus(state.Position, state.Count, wrapped: false);
    }

    private void ReportStatus(int position, int count, bool wrapped)
        => statusChanged(FindStatusText.Compose(
            stopCount: count,
            position: position,
            stopUnit: navigator?.StopUnit,
            scansComplete: AllComplete(),
            hitCap: AnyHitMatchCap(),
            allFailedToOpen: AllFailedToOpen(),
            wrapped: wrapped));

    private bool AnyHitMatchCap()
    {
        foreach (var session in sessions)
        {
            if (session.HitMatchCap)
                return true;
        }

        return false;
    }
}
