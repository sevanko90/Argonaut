using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// The waits every index hands out while its scan is still running (<c>WaitForTokenCountAsync</c>
/// and friends). Several of them are outstanding at once in ordinary use - a document's
/// date-scheme inference waits for its whole sample while a path resolve waits for the next
/// batch - and each has to complete on its OWN target, or on the scan stopping.
///
/// The regression: one shared slot held the only TaskCompletionSource, so a later wait for a
/// larger target overwrote a smaller one's and nothing ever completed the stranded task. That is
/// a hang rather than a delay, and it lands where the tasks are joined -
/// <see cref="IndexedFileSession{T}.Dispose"/> - so closing a still-indexing document froze the
/// UI thread for good. Seen as a testhost stuck at 0% CPU after
/// JsonArrayTableEntryPointTests.LargeStillIndexingArray_WaitsForTheArrayToCloseBeforeResolving.
/// </summary>
public class AppendLogWaitTests
{
    /// <summary>A scan whose "writer" is the test: publishes on demand, stops on demand.</summary>
    private sealed class HandDrivenIndex : AppendLogIndexBase<int>
    {
        public Task WaitForCount(int targetCount) => WaitForCountAsync(targetCount);

        public void Publish(int howMany)
        {
            for (int i = 0; i < howMany; i++)
                items.Add(i);

            OnItemsPublished(items.Count);
        }

        public void Stop() => MarkComplete();
    }

    [Fact]
    public async Task ALargerWaitRegisteredLater_DoesNotStrandTheSmallerOne()
    {
        var index = new HandDrivenIndex();

        var small = index.WaitForCount(2);
        var large = index.WaitForCount(500);

        index.Publish(2);
        await small.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(large.IsCompleted);

        index.Publish(498);
        await large.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ASmallerWaitRegisteredLater_CompletesOnItsOwnTarget()
    {
        // The other order, and the point of per-wait targets: the second wait must not be made
        // to sit behind the first one's much larger target.
        var index = new HandDrivenIndex();

        var large = index.WaitForCount(500);
        var small = index.WaitForCount(2);

        index.Publish(2);
        await small.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(large.IsCompleted);
    }

    [Fact]
    public async Task StoppingTheScan_ReleasesEveryOutstandingWait()
    {
        // What a cancelled scan relies on: targets the file will now never reach still have to
        // let go, because Dispose joins the tasks that are waiting on them.
        var index = new HandDrivenIndex();

        var waits = new[] { index.WaitForCount(2), index.WaitForCount(500), index.WaitForCount(1_000_000) };

        index.Stop();

        await Task.WhenAll(waits).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AWaitPastTheEndOfAFinishedScan_IsAlreadyComplete()
    {
        var index = new HandDrivenIndex();
        index.Publish(3);
        index.Stop();

        await index.WaitForCount(1_000).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ManyOverlappingWaits_EachCompleteAtItsOwnTarget()
    {
        // The writer's hot-path check reads a single number, so it has to be the lowest target
        // outstanding - otherwise a wait quietly slips past its own target and only lands when
        // some later one does.
        var index = new HandDrivenIndex();

        var waits = new Task[20];
        for (int i = 0; i < waits.Length; i++)
            waits[i] = index.WaitForCount((waits.Length - i) * 10);

        for (int published = 10; published <= waits.Length * 10; published += 10)
        {
            index.Publish(10);

            for (int i = 0; i < waits.Length; i++)
            {
                int target = (waits.Length - i) * 10;
                if (target > published)
                {
                    Assert.False(waits[i].IsCompleted);
                    continue;
                }

                await waits[i].WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
}
