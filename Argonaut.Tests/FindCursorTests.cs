using Argonaut.Features.Search;

namespace Argonaut.Tests;

/// <summary>
/// The stop-list mechanism on its own: no files, no mappings, no scans, no dispatcher. Matches
/// are handed in as plain lists and their display order as a fake key function, which is the
/// whole point of <see cref="FindCursor"/> being separate - the three subtle behaviours below
/// (incremental folding, the selection surviving a re-sort, one stop per row) were previously
/// only reachable through a real multi-GB-shaped search.
/// </summary>
public class FindCursorTests
{
    private sealed class FakeSource : IMatchSource
    {
        private readonly List<SearchMatch> matches = new();

        public int MatchCount => matches.Count;

        public SearchMatch GetMatch(int index) => matches[index];

        public FakeSource Add(params long[] offsets)
        {
            foreach (long offset in offsets)
                matches.Add(new SearchMatch(offset, 1));

            return this;
        }
    }

    /// <summary>Byte offset as its own key - what every single-file viewer uses.</summary>
    private static long? ByOffset(int source, SearchMatch match) => match.Offset;

    private static IReadOnlyList<IMatchSource> Sources(params FakeSource[] sources) => sources;

    [Fact]
    public void Move_StepsForwardThenWrapsOnlyWhenComplete()
    {
        var cursor = new FindCursor();
        var source = new FakeSource().Add(10, 20);
        cursor.Reset(1);
        cursor.Fold(Sources(source), ByOffset);

        var first = cursor.Move(1, allComplete: false);
        Assert.True(first.Moved);
        Assert.Equal(10, first.Stop.Key);
        Assert.Equal(1, first.Position);
        Assert.Equal(2, first.Count);

        var second = cursor.Move(1, allComplete: false);
        Assert.True(second.Moved);
        Assert.Equal(20, second.Stop.Key);
        Assert.Equal(2, second.Position);

        // At the end with scans still running: refuse to wrap, so "n of m" cannot go backwards
        // while m is still growing.
        var blocked = cursor.Move(1, allComplete: false);
        Assert.False(blocked.Moved);
        Assert.False(blocked.Wrapped);
        Assert.Equal(2, blocked.Position); // position unchanged

        var wrapped = cursor.Move(1, allComplete: true);
        Assert.True(wrapped.Moved);
        Assert.True(wrapped.Wrapped);
        Assert.Equal(1, wrapped.Position);
    }

    [Fact]
    public void Move_BackwardsWrapsToTheLastStopOnlyWhenComplete()
    {
        var cursor = new FindCursor();
        cursor.Reset(1);
        cursor.Fold(Sources(new FakeSource().Add(10, 20, 30)), ByOffset);

        cursor.Move(1, allComplete: true); // on stop 1

        Assert.False(cursor.Move(-1, allComplete: false).Moved);

        var back = cursor.Move(-1, allComplete: true);
        Assert.True(back.Moved);
        Assert.True(back.Wrapped);
        Assert.Equal(30, back.Stop.Key);
        Assert.Equal(3, back.Position);
    }

    [Fact]
    public void Move_WithNoStops_ReportsNothingRatherThanMoving()
    {
        var cursor = new FindCursor();
        cursor.Reset(1);
        cursor.Fold(Sources(new FakeSource()), ByOffset);

        var move = cursor.Move(1, allComplete: true);
        Assert.False(move.Moved);
        Assert.Equal(0, move.Position);
        Assert.Equal(0, move.Count);
    }

    /// <summary>
    /// The reason the selection is remembered by key rather than by index: a match found later
    /// can sort AHEAD of the selected one (the diff interleaves two documents by display order,
    /// not discovery order), shifting every index after it.
    /// </summary>
    [Fact]
    public void Fold_LateMatchSortingAhead_KeepsTheSelectionOnTheSameStop()
    {
        var cursor = new FindCursor();
        var source = new FakeSource().Add(50, 60);
        cursor.Reset(1);
        cursor.Fold(Sources(source), ByOffset);

        var move = cursor.Move(1, allComplete: false);
        Assert.Equal(50, move.Stop.Key);
        Assert.Equal(1, move.Position);

        source.Add(10, 20); // arrive late, sort first
        var state = cursor.Fold(Sources(source), ByOffset);

        Assert.Equal(4, state.Count);
        Assert.Equal(3, state.Position);            // still on offset 50, now third
        Assert.Equal(60, cursor.Move(1, allComplete: false).Stop.Key); // next is still 60
    }

    [Fact]
    public void Fold_IsIncremental_AndDoesNotDuplicateAlreadyFoldedMatches()
    {
        var cursor = new FindCursor();
        var source = new FakeSource().Add(10, 20);
        cursor.Reset(1);

        Assert.Equal(2, cursor.Fold(Sources(source), ByOffset).Count);
        Assert.Equal(2, cursor.Fold(Sources(source), ByOffset).Count); // nothing new arrived

        source.Add(30);
        Assert.Equal(3, cursor.Fold(Sources(source), ByOffset).Count);
    }

    /// <summary>A null key means the viewer cannot show that match at all - not a stop, and not
    /// counted, so "n of m" reports places find will actually visit.</summary>
    [Fact]
    public void Fold_DropsMatchesTheViewerCannotShow()
    {
        var cursor = new FindCursor();
        cursor.Reset(1);

        var state = cursor.Fold(Sources(new FakeSource().Add(10, 20, 30)),
            (_, match) => match.Offset == 20 ? null : match.Offset);

        Assert.Equal(2, state.Count);
        Assert.Equal(10, cursor.Move(1, allComplete: true).Stop.Key);
        Assert.Equal(30, cursor.Move(1, allComplete: true).Stop.Key);
    }

    /// <summary>Several matches can share one row - a property name and its own value, or a diff
    /// row's two panes - and find stops there once.</summary>
    [Fact]
    public void Fold_CollapsesMatchesSharingAKeyIntoOneStop()
    {
        var cursor = new FindCursor();
        cursor.Reset(1);

        // Three matches, two of which key to the same row.
        var state = cursor.Fold(Sources(new FakeSource().Add(10, 11, 30)),
            (_, match) => match.Offset < 20 ? 1 : 2);

        Assert.Equal(2, state.Count);
    }

    /// <summary>Two sources interleave by key, not by source - the diff's "read down the screen"
    /// requirement, rather than draining one document before starting the other.</summary>
    [Fact]
    public void Fold_InterleavesSourcesByKey()
    {
        var cursor = new FindCursor();
        var left = new FakeSource().Add(10, 40);
        var right = new FakeSource().Add(20, 30);
        cursor.Reset(2);
        cursor.Fold(Sources(left, right), ByOffset);

        Assert.Equal((0, 10L), Step(cursor));
        Assert.Equal((1, 20L), Step(cursor));
        Assert.Equal((1, 30L), Step(cursor));
        Assert.Equal((0, 40L), Step(cursor));

        static (int Source, long Key) Step(FindCursor cursor)
        {
            var move = cursor.Move(1, allComplete: true);
            return (move.Stop.Source, move.Stop.Key);
        }
    }

    [Fact]
    public void Reset_ForgetsStopsAndSelection()
    {
        var cursor = new FindCursor();
        cursor.Reset(1);
        cursor.Fold(Sources(new FakeSource().Add(10, 20)), ByOffset);
        cursor.Move(1, allComplete: true);

        cursor.Reset(0);

        var state = cursor.State();
        Assert.Equal(0, state.Count);
        Assert.Equal(0, state.Position);
        Assert.False(state.HasStopAhead);
    }
}
