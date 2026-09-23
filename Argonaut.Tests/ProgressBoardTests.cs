using Argonaut.Ui.Progress;

namespace Argonaut.Tests;

/// <summary>
/// The rules that stop the progress bar flickering: work that finishes quickly is never shown,
/// work that is shown stays long enough to read, and the bar fades before its text is cleared.
/// Time is stepped by hand, so each rule is checked at its exact edges.
/// </summary>
public sealed class ProgressBoardTests
{
    private sealed class SteppedClock : TimeProvider
    {
        private long ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => this.ticks;

        public void Advance(TimeSpan by) => this.ticks += by.Ticks;
    }

    private readonly SteppedClock clock = new();
    private readonly ProgressBoard board;

    public ProgressBoardTests()
    {
        board = new ProgressBoard(clock);
    }

    private void Advance(TimeSpan by)
    {
        clock.Advance(by);
        board.Tick();
    }

    private static readonly TimeSpan Moment = TimeSpan.FromMilliseconds(10);

    [Fact]
    public void WorkThatFinishesBeforeTheDelay_IsNeverShown()
    {
        var entry = board.Begin("Indexing small.json");
        Advance(ProgressBoard.ShowAfter - Moment);
        entry.Finish();
        Advance(Moment * 2);

        Assert.Empty(board.Shown);
        Assert.False(board.IsVisible);
        Assert.False(board.HasWork);
    }

    [Fact]
    public void WorkStillRunningAfterTheDelay_IsShown()
    {
        var entry = board.Begin("Indexing big.json");
        Advance(ProgressBoard.ShowAfter - Moment);
        Assert.Empty(board.Shown);

        Advance(Moment);

        Assert.Same(entry, Assert.Single(board.Shown));
        Assert.True(board.IsVisible);
    }

    [Fact]
    public void AShownEntry_StaysForItsMinimumTime_EvenWhenItFinishesAtOnce()
    {
        var entry = board.Begin("Indexing big.json");
        Advance(ProgressBoard.ShowAfter);
        entry.Finish();

        Advance(ProgressBoard.ShowAtLeast - Moment);

        Assert.Single(board.Shown);
        Assert.True(board.IsVisible);
    }

    [Fact]
    public void TheLastEntryGoing_FadesTheBarBeforeClearingIt()
    {
        var entry = board.Begin("Indexing big.json");
        Advance(ProgressBoard.ShowAfter);
        entry.Finish();
        Advance(ProgressBoard.ShowAtLeast);

        // Fading: hidden, but the text is still there to fade with it.
        Assert.False(board.IsVisible);
        Assert.Single(board.Shown);

        Advance(ProgressBoard.FadeOut);

        Assert.Empty(board.Shown);
        Assert.False(board.HasWork);
    }

    [Fact]
    public void AFinishedEntry_LeavesAtOnce_WhileAnotherKeepsTheBarUp()
    {
        var first = board.Begin("Indexing big.json");
        var second = board.Begin("Searching big.json");
        Advance(ProgressBoard.ShowAfter);
        first.Finish();

        Advance(ProgressBoard.ShowAtLeast);

        Assert.Same(second, Assert.Single(board.Shown));
        Assert.True(board.IsVisible);
    }

    [Fact]
    public void NewWorkDuringTheFade_BringsTheBarBack()
    {
        var first = board.Begin("Indexing big.json");
        Advance(ProgressBoard.ShowAfter);
        first.Finish();
        Advance(ProgressBoard.ShowAtLeast);
        Assert.False(board.IsVisible);

        var second = board.Begin("Searching big.json");
        Advance(ProgressBoard.ShowAfter);

        Assert.True(board.IsVisible);
        Assert.Same(second, Assert.Single(board.Shown));
    }

    [Fact]
    public void Stop_AsksOnce_AndOnlyWhenTheOperationCanStop()
    {
        int requests = 0;
        var stoppable = board.Begin("Saving big.json", () => requests++);
        var unstoppable = board.Begin("Indexing big.json");

        Assert.True(stoppable.CanStop);
        Assert.False(unstoppable.CanStop);

        stoppable.RequestStop();
        stoppable.RequestStop();
        unstoppable.RequestStop();

        Assert.Equal(1, requests);
        Assert.False(stoppable.CanStop);
    }

    [Fact]
    public async Task FinishWhen_FinishesOnSuccessFailureAndCancellation()
    {
        var succeeded = board.Begin("a");
        var failed = board.Begin("b");
        var cancelled = board.Begin("c");

        succeeded.FinishWhen(Task.CompletedTask);
        failed.FinishWhen(Task.FromException(new IOException("gone")));
        cancelled.FinishWhen(Task.FromCanceled(new CancellationToken(canceled: true)));
        await Task.Yield();

        Assert.True(succeeded.IsFinished);
        Assert.True(failed.IsFinished);
        Assert.True(cancelled.IsFinished);
    }

    [Fact]
    public void Begin_RaisesWorkStarted_SoTheWindowStartsTicking()
    {
        int started = 0;
        board.WorkStarted += (_, _) => started++;

        board.Begin("Indexing big.json");

        Assert.Equal(1, started);
        Assert.True(board.HasWork);
    }
}
