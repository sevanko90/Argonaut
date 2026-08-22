using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// The growth monitor is documented as UI-thread-only, and under a real dispatcher it is: the
/// completion await resumes where it started. Tests host it without one, so that continuation
/// resumes on whatever thread completed the scan and can run alongside the owner's Dispose -
/// which is exactly what a real teardown does when a view model is disposed as its scan ends.
/// Both paths tear the timer down, so the teardown has to be claimed atomically rather than
/// guarded by a null check.
/// </summary>
public class IndexGrowthMonitorTests
{
    [Fact]
    public async Task CompletionRacingDispose_DoesNotThrow_AndTearsDownOnce()
    {
        // Repeated because it is a race: a single pass would usually take one interleaving and
        // miss the window between the guard and the field it guarded.
        for (int i = 0; i < 500; i++)
        {
            TaskCompletionSource completion = new();
            int refreshes = 0;

            IndexGrowthMonitor monitor = new(
                TimeSpan.FromMilliseconds(50),
                completion.Task,
                isComplete: () => true,
                refresh: () => Interlocked.Increment(ref refreshes));

            using ManualResetEventSlim ready = new();
            Task completer = Task.Run(() =>
            {
                ready.Wait();
                completion.SetResult();
            });

            ready.Set();
            monitor.Dispose(); // races the completion continuation
            await completer;

            // Exactly one caller owns the teardown, so the final refresh happens at most once
            // however the two threads interleave. (At-most, not exactly: Dispose legitimately
            // wins the claim and cancels the pending final refresh.)
            Assert.InRange(Volatile.Read(ref refreshes), 0, 1);
        }
    }
}
