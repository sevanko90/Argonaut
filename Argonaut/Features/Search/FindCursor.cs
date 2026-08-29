using System;
using System.Collections.Generic;
using System.Threading;

namespace Argonaut.Features.Search;

/// <summary>One place find will stop: a match, plus where it sorts in the merged order.</summary>
public readonly record struct FindStop(int Source, int MatchIndex, long Key);

/// <summary><see cref="Position"/> is 1-based; 0 means no stop is selected (nothing found yet,
/// or the selected one was collapsed away by a fold).</summary>
public readonly record struct FindCursorState(int Position, int Count, bool HasStopAhead);

/// <summary>
/// <see cref="Moved"/> false means the press could not advance: no stops, or the end reached
/// while scans still run. Position/Count are filled either way, so a caller can report status
/// without a second, racy read.
/// </summary>
public readonly record struct FindCursorMove(bool Moved, FindStop Stop, bool Wrapped, int Position, int Count);

/// <summary>
/// The ordered list of places find can land, and where it currently is.
///
/// Knows nothing about files, scans, view models or the dispatcher: matches arrive through
/// <see cref="IMatchSource"/> and their display order as an OPAQUE long from the caller's key
/// function (<see cref="ISearchNavigator.OrderKey"/> in the app). Policy stays with the viewer;
/// the mechanism here is pure, and directly testable.
///
/// Two things drive its design, both consequences of matches still arriving while the user
/// steps through them:
///
/// - **The position cannot be an index.** A match found late can sort AHEAD of the selected one
///   (the diff interleaves two documents by display order, not discovery order), shifting every
///   index after it. So the selection is remembered by key and re-found after each fold.
/// - **Equal keys are one stop.** A property name and its own value, or a diff row's two panes,
///   are one place on screen - so the count reports what find will visit, not how often the
///   bytes occur.
///
/// One lock covers everything: presses come from the UI thread, but a completion refresh folds
/// from wherever its await resumed (a pool thread in a dispatcher-free test). It is never held
/// across an await - there are none here.
/// </summary>
public sealed class FindCursor
{
    private readonly Lock gate = new();

    private readonly List<FindStop> stops = new();

    /// <summary>Fold high-water mark per source, so each match is keyed exactly once.</summary>
    private int[] foldedCounts = Array.Empty<int>();

    private int position = -1;

    // The selection, held by key because an index would not survive a re-sort - see the remarks.
    private long currentKey;
    private bool hasCurrent;

    /// <summary>Starts over for a new set of sources - call when the term changes or find stops.</summary>
    public void Reset(int sourceCount)
    {
        lock (gate)
        {
            stops.Clear();
            foldedCounts = sourceCount == 0 ? Array.Empty<int>() : new int[sourceCount];
            position = -1;
            currentKey = 0;
            hasCurrent = false;
        }
    }

    /// <summary>
    /// Folds in everything found since the last call, re-sorts, and re-finds the selection.
    /// Returns the resulting state so a caller never needs a second, racy read.
    /// </summary>
    /// <param name="sources">Must match the count passed to <see cref="Reset"/>.</param>
    /// <param name="keyOf">Where a match sorts, or null for one the viewer cannot show at all -
    /// not a stop, and skipping it loses nothing, since nothing displays it.</param>
    public FindCursorState Fold(IReadOnlyList<IMatchSource> sources, Func<int, SearchMatch, long?> keyOf)
    {
        lock (gate)
        {
            FoldLocked(sources, keyOf);
            return StateLocked();
        }
    }

    /// <summary>
    /// Steps one stop forward (<paramref name="direction"/> &gt;= 0) or back, wrapping only when
    /// <paramref name="allComplete"/> - a wrap mid-scan would make "n of m" jump backwards while
    /// m is still growing.
    /// </summary>
    public FindCursorMove Move(int direction, bool allComplete)
    {
        lock (gate)
        {
            if (stops.Count == 0)
                return new FindCursorMove(false, default, false, 0, 0);

            bool wrapped = false;

            if (direction >= 0)
            {
                if (position + 1 < stops.Count)
                {
                    position++;
                }
                else if (allComplete)
                {
                    position = 0;
                    wrapped = true;
                }
                else
                {
                    return new FindCursorMove(false, default, false, PositionLocked(), stops.Count);
                }
            }
            else
            {
                if (position > 0)
                {
                    position--;
                }
                else if (allComplete)
                {
                    position = stops.Count - 1;
                    wrapped = true;
                }
                else
                {
                    return new FindCursorMove(false, default, false, PositionLocked(), stops.Count);
                }
            }

            var stop = stops[position];
            currentKey = stop.Key;
            hasCurrent = true;
            return new FindCursorMove(true, stop, wrapped, PositionLocked(), stops.Count);
        }
    }

    /// <summary>Current position and count without folding or moving.</summary>
    public FindCursorState State()
    {
        lock (gate)
        {
            return StateLocked();
        }
    }

    private FindCursorState StateLocked()
        => new FindCursorState(PositionLocked(), stops.Count, position + 1 < stops.Count);

    private int PositionLocked() => position >= 0 ? position + 1 : 0;

    private void FoldLocked(IReadOnlyList<IMatchSource> sources, Func<int, SearchMatch, long?> keyOf)
    {
        bool grown = false;

        for (int i = 0; i < sources.Count; i++)
        {
            int count = sources[i].MatchCount;
            for (int m = foldedCounts[i]; m < count; m++)
            {
                if (keyOf(i, sources[i].GetMatch(m)) is { } key)
                    stops.Add(new FindStop(i, m, key));
            }

            if (count != foldedCounts[i])
            {
                foldedCounts[i] = count;
                grown = true;
            }
        }

        if (!grown)
            return;

        // Tie-broken so equal keys keep a stable order instead of shuffling on every fold.
        stops.Sort(static (a, b) =>
        {
            int byKey = a.Key.CompareTo(b.Key);
            if (byKey != 0)
                return byKey;

            int bySource = a.Source.CompareTo(b.Source);
            return bySource != 0 ? bySource : a.MatchIndex.CompareTo(b.MatchIndex);
        });

        // Equal keys mean the same row by construction - the diff's RowOrderKey ignores which
        // pane matched, and everything below it keys by token, one token per row.
        int write = 0;
        for (int read = 0; read < stops.Count; read++)
        {
            if (write > 0 && stops[write - 1].Key == stops[read].Key)
                continue;

            stops[write++] = stops[read];
        }

        stops.RemoveRange(write, stops.Count - write);

        position = -1;
        if (!hasCurrent)
            return;

        for (int i = 0; i < stops.Count; i++)
        {
            if (stops[i].Key == currentKey)
            {
                position = i;
                return;
            }
        }
    }
}
