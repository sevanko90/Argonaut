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
/// A document may expose more than one file (the diff's two sides). Each gets its own session
/// and its own cursor, and stepping picks whichever side's next match comes first in
/// <see cref="ISearchNavigator.OrderKey"/> order - so one find bar walks both documents as a
/// single sequence, in display order, rather than draining one before starting the other.
/// The single-file case is that machinery with one entry, and behaves exactly as before.
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

    /// <summary>
    /// Per-file cursor, holding this invariant: <c>cursors[i]</c> is the index of the last
    /// match in file <c>i</c> whose key is at or before the current selection's key (-1 when
    /// that file has nothing at or before it yet). So the next candidate going forward is
    /// always <c>cursors[i] + 1</c>, and going back it is <c>cursors[i]</c> - except in the
    /// file the selection itself came from, where it is one earlier.
    /// </summary>
    private int[] cursors = Array.Empty<int>();

    private int currentFile = -1;
    private long currentKey;

    /// <summary>Position of the selection in the merged sequence, or -1 before the first step.
    /// Tracked incrementally rather than recomputed: the merged sequence is never materialized.</summary>
    private int mergedOrdinal = -1;

    private long requestId;
    private CancellationTokenSource? revealCts;

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
    /// </summary>
    public async Task FindAsync(string term, int direction)
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
            cursors = new int[files.Count];
            for (int i = 0; i < files.Count; i++)
            {
                sessions[i] = FileSearchSession.Start(files[i], new LiteralSearchMatcher(term), progressReporterFactory());
                cursors[i] = -1;
            }

            currentFile = -1;
            mergedOrdinal = -1;
            navigator.SetHighlightTerm(term);
            _ = RefreshStatusOnCompletionAsync(sessions, request);
        }

        var active = sessions;
        bool wrapped;

        if (direction >= 0)
        {
            // Wait for each side's next match to stream in; never wrap while any scan is still
            // running, so "n of m" stays monotone.
            for (int i = 0; i < active.Length; i++)
            {
                if (!await EnsureForwardCandidateAsync(i, request))
                    return;
            }

            if (!TryStepForward(active, out wrapped))
            {
                UpdateStatus(active, wrapped: false);
                return;
            }
        }
        else
        {
            if (!TryStepBackward(active, out wrapped))
            {
                UpdateStatus(active, wrapped: false);
                return;
            }
        }

        UpdateStatus(active, wrapped);

        var cts = new CancellationTokenSource();
        revealCts = cts;
        try
        {
            await navigator.RevealAsync(currentFile, active[currentFile].GetMatch(cursors[currentFile]), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // A newer request or a stop superseded this reveal.
        }
    }

    /// <summary>
    /// Waits until file <paramref name="i"/> either has a navigable match to offer beyond its
    /// cursor or has finished scanning. Matches the viewer cannot show (a null
    /// <see cref="ISearchNavigator.OrderKey"/>) are consumed by moving the cursor over them, so
    /// a long run of them costs one scan in total rather than one per find press.
    /// False when a newer request took over mid-wait.
    /// </summary>
    private async Task<bool> EnsureForwardCandidateAsync(int i, long request)
    {
        var session = sessions[i];
        while (true)
        {
            while (cursors[i] + 1 < session.MatchCount)
            {
                if (navigator!.OrderKey(i, session.GetMatch(cursors[i] + 1)) is not null)
                    return true;

                cursors[i]++;
            }

            if (session.IsComplete)
                return true;

            statusChanged("Searching…");
            await session.WaitForMatchCountAsync(cursors[i] + 2);
            if (request != requestId)
                return false;
        }
    }

    /// <summary>
    /// Advances to the earliest next match across all files, wrapping to the very first match
    /// when every file is exhausted. False when there is nothing to select at all.
    /// </summary>
    private bool TryStepForward(FileSearchSession[] active, out bool wrapped)
    {
        wrapped = false;

        if (!TryPickForward(active, out int file))
        {
            // Nothing further ahead. Wrapping needs every scan finished, or a match found
            // after the wrap could turn out to belong before the one we jumped to.
            if (!AllComplete(active) || TotalMatches(active) == 0)
                return false;

            for (int i = 0; i < cursors.Length; i++)
                cursors[i] = -1;

            if (!TryPickForward(active, out file))
                return false;

            wrapped = true;
        }

        cursors[file]++;
        currentFile = file;
        currentKey = KeyAt(active, file, cursors[file]) ?? long.MaxValue;
        mergedOrdinal = wrapped ? 0 : mergedOrdinal + 1;
        return true;
    }

    /// <summary>Lowest-keyed candidate among each file's next navigable match. Skipped matches
    /// are stepped over here too, for the wrap case that re-enters without the ensure pass.</summary>
    private bool TryPickForward(FileSearchSession[] active, out int file)
    {
        file = -1;
        long best = 0;

        for (int i = 0; i < active.Length; i++)
        {
            while (cursors[i] + 1 < active[i].MatchCount
                && navigator!.OrderKey(i, active[i].GetMatch(cursors[i] + 1)) is null)
            {
                cursors[i]++;
            }

            int candidate = cursors[i] + 1;
            if (candidate >= active[i].MatchCount)
                continue;

            long key = KeyAt(active, i, candidate) ?? long.MaxValue;
            if (file < 0 || key < best)
            {
                file = i;
                best = key;
            }
        }

        return file >= 0;
    }

    /// <summary>
    /// Steps back to the latest match before the current selection, wrapping to the very last
    /// match when there is nothing before it. False when there is nothing to select.
    /// </summary>
    private bool TryStepBackward(FileSearchSession[] active, out bool wrapped)
    {
        wrapped = false;

        int total = TotalMatches(active);
        if (total == 0)
            return false;

        int file = -1;
        int chosen = -1;
        long best = 0;

        for (int i = 0; i < active.Length; i++)
        {
            // Every file's cursor sits at or before the selection; only the file the selection
            // came from sits exactly ON it, so that one has to step an extra place back.
            int candidate = PreviousNavigable(active, i, i == currentFile ? cursors[i] - 1 : cursors[i], out long key);
            if (candidate < 0)
                continue;

            if (file < 0 || key > best)
            {
                file = i;
                chosen = candidate;
                best = key;
            }
        }

        if (file < 0)
        {
            // Wrapping backward lands on the last match overall, so it needs the full lists.
            if (!AllComplete(active))
                return false;

            for (int i = 0; i < active.Length; i++)
            {
                int candidate = PreviousNavigable(active, i, active[i].MatchCount - 1, out long key);
                if (candidate < 0)
                    continue;

                if (file < 0 || key > best)
                {
                    file = i;
                    chosen = candidate;
                    best = key;
                }
            }

            if (file < 0)
                return false;

            wrapped = true;
        }

        currentFile = file;
        currentKey = best;
        cursors[file] = chosen;

        // Restore the cursor invariant everywhere else: no other file may still point past the
        // new selection. One step back normally moves each by at most one; a wrap resets them
        // from the end, which this same walk brings down to the right place. A skipped match
        // has no key and is simply stepped over.
        for (int i = 0; i < active.Length; i++)
        {
            if (i == file)
                continue;

            if (wrapped)
                cursors[i] = active[i].MatchCount - 1;

            while (cursors[i] >= 0)
            {
                long? key = KeyAt(active, i, cursors[i]);
                if (key is { } value && value <= currentKey)
                    break;

                cursors[i]--;
            }
        }

        mergedOrdinal = wrapped ? total - 1 : mergedOrdinal - 1;
        return true;
    }

    /// <summary>Walks down from <paramref name="from"/> to the first match the viewer can show,
    /// or -1 when there is none at or before it.</summary>
    private int PreviousNavigable(FileSearchSession[] active, int file, int from, out long key)
    {
        for (int i = Math.Min(from, active[file].MatchCount - 1); i >= 0; i--)
        {
            if (KeyAt(active, file, i) is { } found)
            {
                key = found;
                return i;
            }
        }

        key = 0;
        return -1;
    }

    private long? KeyAt(FileSearchSession[] active, int file, int index)
        => navigator!.OrderKey(file, active[file].GetMatch(index));

    private static int TotalMatches(FileSearchSession[] active)
    {
        int total = 0;
        foreach (var session in active)
            total += session.MatchCount;
        return total;
    }

    private static bool AllComplete(FileSearchSession[] active)
    {
        foreach (var session in active)
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
        var old = sessions;
        sessions = Array.Empty<FileSearchSession>();
        cursors = Array.Empty<int>();
        sessionTerm = null;
        currentFile = -1;
        mergedOrdinal = -1;

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
    /// Refreshes the "n of m (searching…)" status once every scan finishes, so the count
    /// stops advertising an in-progress search that already ended.
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

        UpdateStatus(tracked, wrapped: false);
    }

    private void UpdateStatus(FileSearchSession[] active, bool wrapped)
    {
        int total = TotalMatches(active);
        bool complete = AllComplete(active);

        string text;
        if (total == 0)
        {
            text = complete ? "No matches" : "Searching…";
        }
        else
        {
            text = mergedOrdinal >= 0
                ? $"{mergedOrdinal + 1:N0} of {total:N0}"
                : $"{total:N0} matches";

            if (!complete)
            {
                text += " (searching…)";
            }
            else
            {
                bool capped = false;
                foreach (var session in active)
                    capped |= session.HitMatchCap;

                if (capped)
                    text += $" (first {total:N0} only)";
            }

            if (wrapped)
                text += " — wrapped";
        }

        statusChanged(text);
    }
}
