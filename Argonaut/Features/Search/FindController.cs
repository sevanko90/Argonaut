using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Search;

/// <summary>
/// UI-side orchestration of find / find next for the currently open document. Owns the search
/// session lifecycle (one per searched file; a changed term or a stop cancels the previous
/// scans), the result cursor and wrap semantics, and hands each chosen match to the attached
/// <see cref="ISearchNavigator"/> to reveal.
///
/// A document may expose more than one file (the diff's two sides). Rather than step several
/// cursors in lockstep, every match that <see cref="ISearchNavigator.OrderKey"/> accepts is
/// folded into ONE list of stops ordered by that key. Find then walks a single list by index -
/// which is both the original single-file logic unchanged and the only way the "n of m" count
/// can be honest: m counts the places find will actually stop, not the times the bytes occur.
/// Those differ whenever the viewer cannot show a match; see OrderKey for when that happens.
///
/// All public members run on the UI thread; awaits resume there, and a monotonic request id
/// (the codebase's staleness idiom) guards every post-await continuation against a newer
/// request having taken over.
/// </summary>
public sealed class FindController
{
    /// <summary>One place find will stop: which file's scan produced it, which match it is in
    /// that scan, and where it sorts in the merged order.</summary>
    private readonly record struct Stop(int File, int MatchIndex, long Key);

    private readonly Action<string?> statusChanged;
    private readonly Func<IProgressReporter?> progressReporterFactory;

    private ISearchNavigator? navigator;
    private FileSearchSession[] sessions = Array.Empty<FileSearchSession>();
    private string? sessionTerm;

    private readonly List<Stop> stops = new();

    /// <summary>
    /// Guards <see cref="stops"/>, <see cref="foldedCounts"/> and <see cref="cursor"/>. Presses
    /// run on the UI thread, but <see cref="RefreshStatusOnCompletionAsync"/> is fire-and-forget
    /// and folds the last matches in from wherever its await resumes - the dispatcher thread in
    /// the app, a pool thread in a dispatcher-free test. Unguarded, both could fold the same
    /// matches and the stop list doubled. Never held across an await.
    /// </summary>
    private readonly object gate = new();

    /// <summary>How many of each file's matches have been folded into <see cref="stops"/>, so a
    /// refresh only ever costs the ones that arrived since.</summary>
    private int[] foldedCounts = Array.Empty<int>();

    private int cursor = -1;

    // The selected stop's identity, which survives a re-sort; the cursor index does not, since
    // a match arriving late can sort ahead of it.
    private int currentFileIndex = -1;
    private int currentMatchIndex = -1;

    private long requestId;
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
    /// searchable. Call after <see cref="StopAsync"/>.
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

        long request = ++requestId;
        CancelReveal();

        if (sessions.Length == 0 || !string.Equals(term, sessionTerm, StringComparison.Ordinal))
        {
            await DisposeSessionsAsync();
            if (request != requestId)
                return;

            var files = navigator.Files;
            sessionTerm = term;
            sessions = new FileSearchSession[files.Count];
            foldedCounts = new int[files.Count];
            for (int i = 0; i < files.Count; i++)
                sessions[i] = FileSearchSession.Start(files[i], new LiteralSearchMatcher(term), progressReporterFactory());

            navigator.SetHighlightTerm(term);
            _ = RefreshStatusOnCompletionAsync(sessions, request);
        }

        // Going forward, wait for a stop past the cursor to turn up. Going back, whatever has
        // been found already is all there is to step onto.
        if (direction >= 0)
        {
            if (!await EnsureStopAfterCursorAsync(request))
                return;
        }
        else
        {
            RefreshStops();
        }

        // Decided in one go under the gate so the completion refresh cannot fold new stops in
        // between choosing the index and reading it back.
        Stop stop;
        bool wrapped = false;
        lock (gate)
        {
            if (stops.Count == 0)
            {
                UpdateStatusLocked(wrapped: false);
                return;
            }

            if (direction >= 0)
            {
                if (cursor + 1 < stops.Count)
                {
                    cursor++;
                }
                else if (AllComplete())
                {
                    cursor = 0;
                    wrapped = true;
                }
                else
                {
                    // Never wrap while a scan is still running, so "n of m" stays monotone.
                    UpdateStatusLocked(wrapped: false);
                    return;
                }
            }
            else
            {
                if (cursor > 0)
                {
                    cursor--;
                }
                else if (AllComplete())
                {
                    // Wrapping backward lands on the last stop, so it needs the full list.
                    cursor = stops.Count - 1;
                    wrapped = true;
                }
                else
                {
                    UpdateStatusLocked(wrapped: false);
                    return;
                }
            }

            stop = stops[cursor];
            currentFileIndex = stop.File;
            currentMatchIndex = stop.MatchIndex;
            UpdateStatusLocked(wrapped);
        }

        var cts = new CancellationTokenSource();
        revealCts = cts;
        try
        {
            await navigator.RevealAsync(stop.File, sessions[stop.File].GetMatch(stop.MatchIndex), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // A newer request or a stop superseded this reveal.
        }
    }

    /// <summary>
    /// Waits until there is a stop past the cursor, or every scan has finished. False when a
    /// newer request took over mid-wait. Matches the viewer cannot show are folded away by
    /// <see cref="RefreshStops"/>, so a long run of them costs one pass, not one wait each.
    /// </summary>
    private async Task<bool> EnsureStopAfterCursorAsync(long request)
    {
        while (true)
        {
            // Always fold before deciding. Scans can finish between the check below and the
            // waits being built, and an early exit that skipped this reported "No matches"
            // over results that had in fact just landed.
            bool haveStop;
            lock (gate)
            {
                RefreshStopsLocked();
                haveStop = cursor + 1 < stops.Count;
            }

            if (haveStop || AllComplete())
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
            if (request != requestId)
                return false;
        }
    }

    /// <summary>
    /// Folds every match found since the last call into <see cref="stops"/>, dropping the ones
    /// the viewer cannot show, and restores the ordering. Only newly arrived matches are keyed,
    /// so across a whole search this costs one <c>OrderKey</c> per match.
    ///
    /// The re-sort only happens while scans are still streaming; once they finish the list is
    /// final and stepping is pure indexing. The cursor is re-derived from the selected stop's
    /// identity rather than kept, because a match found late can sort ahead of it.
    /// </summary>
    private void RefreshStops()
    {
        lock (gate)
        {
            RefreshStopsLocked();
        }
    }

    private void RefreshStopsLocked()
    {
        bool grown = false;

        for (int i = 0; i < sessions.Length; i++)
        {
            int count = sessions[i].MatchCount;
            for (int m = foldedCounts[i]; m < count; m++)
            {
                if (navigator!.OrderKey(i, sessions[i].GetMatch(m)) is { } key)
                    stops.Add(new Stop(i, m, key));
            }

            if (count != foldedCounts[i])
            {
                foldedCounts[i] = count;
                grown = true;
            }
        }

        if (!grown)
            return;

        // Tie-broken so equal keys (several unresolvable matches share one) keep a stable order
        // instead of shuffling on every refresh.
        stops.Sort(static (a, b) =>
        {
            int byKey = a.Key.CompareTo(b.Key);
            if (byKey != 0)
                return byKey;

            int byFile = a.File.CompareTo(b.File);
            return byFile != 0 ? byFile : a.MatchIndex.CompareTo(b.MatchIndex);
        });

        cursor = -1;
        if (currentFileIndex < 0)
            return;

        for (int i = 0; i < stops.Count; i++)
        {
            if (stops[i].File == currentFileIndex && stops[i].MatchIndex == currentMatchIndex)
            {
                cursor = i;
                return;
            }
        }
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
    /// Stops the active search: cancels any in-flight reveal, cancels the background scans
    /// and waits for them to let go of the files, and clears row highlighting. MUST complete
    /// before the current view's MMapFile is disposed - a scan thread touching a disposed
    /// mapping is an access violation.
    /// </summary>
    public async Task StopAsync()
    {
        ++requestId;
        CancelReveal();
        navigator?.SetHighlightTerm(null);
        statusChanged(null);
        await DisposeSessionsAsync();
    }

    /// <summary>Stops the active search and forgets the current document's navigator.</summary>
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

    private async Task DisposeSessionsAsync()
    {
        FileSearchSession[] old;
        lock (gate)
        {
            old = sessions;
            sessions = Array.Empty<FileSearchSession>();
            foldedCounts = Array.Empty<int>();
            sessionTerm = null;
            stops.Clear();
            cursor = -1;
            currentFileIndex = -1;
            currentMatchIndex = -1;
        }

        foreach (var session in old)
            session.Cancel();

        foreach (var session in old)
        {
            try
            {
                await session.ScanTask;
            }
            catch
            {
                // A failed scan has nothing further to release; surfacing it here would only
                // break the stop path.
            }
        }
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

        if (request != requestId)
            return;

        foreach (var session in tracked)
        {
            if (session.WasCancelled)
                return;
        }

        RefreshStops();
        UpdateStatus(wrapped: false);
    }

    private void UpdateStatus(bool wrapped)
    {
        lock (gate)
        {
            UpdateStatusLocked(wrapped);
        }
    }

    private void UpdateStatusLocked(bool wrapped)
    {
        bool complete = AllComplete();

        string text;
        if (stops.Count == 0)
        {
            text = complete ? "No matches" : "Searching…";
        }
        else
        {
            text = cursor >= 0
                ? $"{cursor + 1:N0} of {stops.Count:N0}"
                : $"{stops.Count:N0} matches";

            if (!complete)
            {
                text += " (searching…)";
            }
            else
            {
                bool capped = false;
                foreach (var session in sessions)
                    capped |= session.HitMatchCap;

                if (capped)
                    text += $" (first {stops.Count:N0} only)";
            }

            if (wrapped)
                text += " — wrapped";
        }

        statusChanged(text);
    }
}
