using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Search;
using Argonaut.Tests.Support;
using Argonaut.Ui.Find;

namespace Argonaut.Tests.Ui.Find;

/// <summary>
/// Exercises the find orchestration against real temp files and scans: cursor stepping and
/// wrap-around, backward stepping, no-match status, term changes superseding the previous
/// session, stop retiring the scans, and the reveal hand-off to the navigator - including its
/// cancellation when the owning document starts tearing down. FindController itself is UI-framework-free (delegates +
/// ISearchNavigator), so these run as plain unit tests; per the app convention its awaits
/// just resume on the caller's context.
/// </summary>
public class FindControllerTests
{
    private sealed class StubNavigator(string path) : ISearchNavigator
    {
        private readonly CancellationTokenSource tearingDown = new();

        public ScanTarget ScanTarget { get; } = LoadFromPath.ScanTargetFor(path);
        public List<string?> HighlightTerms { get; } = new();
        public List<SearchMatch> Revealed { get; } = new();

        public CancellationToken DocumentTearingDown => tearingDown.Token;

        /// <summary>Stands in for the document's own Dispose starting teardown - the path that
        /// never goes through the controller (a view's detach handler on window close).</summary>
        public void StartTearingDown() => tearingDown.Cancel();

        /// <summary>Set to block inside the reveal, so a teardown can land while one is in
        /// flight rather than only between them.</summary>
        public TaskCompletionSource? RevealGate { get; set; }

        public void SetHighlightTerm(string? term) => HighlightTerms.Add(term);

        public async Task RevealAsync(SearchMatch match, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (RevealGate is { } gate)
                await gate.Task.WaitAsync(ct);

            ct.ThrowIfCancellationRequested();
            Revealed.Add(match);
        }
    }

    /// <summary>
    /// The half of the old scope wiring that survives: scans are nobody's dependents any more,
    /// but a REVEAL still touches document-owned state, so it must stop when the document
    /// starts tearing down - including the paths that never reach the controller at all.
    /// </summary>
    [Fact]
    public async Task Reveal_IsCancelled_WhenDocumentStartsTearingDown()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("abc needle abc needle abc"));
            var navigator = new StubNavigator(path) { RevealGate = new TaskCompletionSource() };
            var controller = new FindController(_ => { }, () => null);
            controller.Attach(navigator);

            var find = controller.FindAsync("needle", direction: 1);
            navigator.StartTearingDown();

            // Completes without throwing: the reveal observes the linked token and the
            // controller swallows the cancellation.
            await find.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(navigator.Revealed);

            controller.Detach();
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Serves the first chunk at once and holds every later one until released - a multi-GB
    /// scan that is still running, without needing gigabytes. A term missing from the first
    /// chunk therefore leaves its press waiting for as long as the test likes.
    /// </summary>
    private sealed class HeldBackOrigin(byte[] bytes) : IByteOrigin
    {
        private readonly MemoryByteOrigin inner = new(bytes, "held back");

        public ManualResetEventSlim Release { get; } = new();

        public string DisplayName => inner.DisplayName;
        public string? Path => null;
        public long AvailableLength => inner.AvailableLength;
        public IByteSource Open() => inner.Open();
        public void Dispose() => Release.Set();

        public IByteSource OpenRange(long offset, long length)
        {
            if (offset > 0)
                Release.Wait(TimeSpan.FromSeconds(30));

            return inner.OpenRange(offset, length);
        }
    }

    private sealed class OriginNavigator(IByteOrigin origin) : ISearchNavigator
    {
        public ScanTarget ScanTarget { get; } = new(origin);
        public List<SearchMatch> Revealed { get; } = new();
        public CancellationToken DocumentTearingDown => default;
        public void SetHighlightTerm(string? term) { }

        public Task RevealAsync(SearchMatch match, CancellationToken ct)
        {
            Revealed.Add(match);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// The bug: presses are serialised, and a press waiting for the first match of a term the
    /// scan has not found yet held a new term's press in the queue until the old scan found
    /// something or reached the end of a 4GB file. A different term must interrupt it instead.
    /// </summary>
    [Fact]
    public async Task ANewTerm_InterruptsAPressStillWaitingForTheOldOne()
    {
        var content = new byte[3 * 4 * 1024 * 1024];
        Array.Fill(content, (byte)'a');
        Encoding.UTF8.GetBytes("needle").CopyTo(content, 100);
        using var origin = new HeldBackOrigin(content);
        var navigator = new OriginNavigator(origin);
        var controller = new FindController(_ => { }, () => null);
        controller.Attach(navigator);

        try
        {
            var waitingForAbsent = controller.FindAsync("absent", direction: 1);
            var newTerm = controller.FindAsync("needle", direction: 1);

            // Both presses finish while the old scan is still held part way through the file.
            await Task.WhenAll(waitingForAbsent, newTerm).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(new SearchMatch(100, 6), Assert.Single(navigator.Revealed));
        }
        finally
        {
            origin.Release.Set();
            controller.Detach();
        }
    }

    /// <summary>Holding Enter on one term still steps match by match, rather than each repeat
    /// cancelling the one before it.</summary>
    [Fact]
    public async Task RepeatedPressesOfTheSameTerm_StillEachAdvance()
    {
        await WithController("needle x needle x needle", async (controller, navigator, _) =>
        {
            var presses = new[]
            {
                controller.FindAsync("needle", 1),
                controller.FindAsync("needle", 1),
                controller.FindAsync("needle", 1),
            };
            await Task.WhenAll(presses).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(new long[] { 0, 9, 18 }, navigator.Revealed.Select(m => m.Offset));
        });
    }

    /// <summary>
    /// A save cannot replace the file while a scan still holds a chunk mapping of it, so it waits
    /// for every scan - including ones retired earlier by a term change, which nothing else joins.
    /// </summary>
    [Fact]
    public async Task StopSearchAndWait_CompletesOnlyOnceEveryScanHasLetGo()
    {
        string path = Path.GetTempFileName();
        try
        {
            // Big enough that the scans are still running when they are stopped.
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(new string('a', 32 * 1024 * 1024) + "needle"));
            var navigator = new StubNavigator(path);
            var controller = new FindController(_ => { }, () => null);
            controller.Attach(navigator);

            _ = controller.FindAsync("first", direction: 1);
            _ = controller.FindAsync("second", direction: 1);

            await controller.StopSearchAndWaitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            // Nothing is left holding the file: on Windows this delete would fail otherwise.
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static async Task WithController(string content,
        Func<FindController, StubNavigator, List<string?>, Task> test)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
            var navigator = new StubNavigator(path);
            var statuses = new List<string?>();
            var controller = new FindController(statuses.Add, () => null);
            controller.Attach(navigator);

            await test(controller, navigator, statuses);

            controller.Detach();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FindNext_StepsThroughMatchesAndWraps()
    {
        await WithController("abc needle abc needle abc", async (controller, navigator, statuses) =>
        {
            await controller.FindAsync("needle", direction: 1);
            await controller.FindAsync("needle", direction: 1);
            Assert.Equal(2, navigator.Revealed.Count);
            Assert.Equal(4, navigator.Revealed[0].Offset);
            Assert.Equal(15, navigator.Revealed[1].Offset);

            // Third find wraps back to the first match.
            await controller.FindAsync("needle", direction: 1);
            Assert.Equal(4, navigator.Revealed[2].Offset);
            Assert.Contains(statuses, s => s is not null && s.Contains("wrapped"));
        });
    }

    [Fact]
    public async Task FindPrevious_StepsBackwardAndWrapsToLast()
    {
        await WithController("x term y term z", async (controller, navigator, statuses) =>
        {
            await controller.FindAsync("term", direction: 1);   // cursor -> 0
            await controller.FindAsync("term", direction: 1);   // cursor -> 1
            await controller.FindAsync("term", direction: -1);  // cursor -> 0
            Assert.Equal(navigator.Revealed[0], navigator.Revealed[2]);

            // At the first match, stepping back wraps to the last (scan is complete by now).
            await controller.FindAsync("term", direction: -1);
            Assert.Equal(navigator.Revealed[1], navigator.Revealed[3]);
        });
    }

    [Fact]
    public async Task NoMatches_ReportsStatusWithoutReveal()
    {
        await WithController("nothing to see here", async (controller, navigator, statuses) =>
        {
            await controller.FindAsync("absent", direction: 1);

            // The scan may still be finishing when FindAsync returns; the completion
            // refresh callback posts the final status.
            await Task.Delay(50);
            Assert.Empty(navigator.Revealed);
            Assert.Contains(statuses, s => s == "No matches" || s == "Searching…");
        });
    }

    [Fact]
    public async Task ChangedTerm_StartsFreshSessionAndResetsCursor()
    {
        await WithController("aa bb aa bb", async (controller, navigator, statuses) =>
        {
            await controller.FindAsync("aa", direction: 1);
            await controller.FindAsync("bb", direction: 1);

            // New term highlights and reveals its own first match, not the old cursor's next.
            Assert.Equal(new[] { "aa", "bb" }, navigator.HighlightTerms.ToArray());
            Assert.Equal(3, navigator.Revealed[1].Offset);
        });
    }

    [Fact]
    public async Task StopSearch_ClearsHighlightAndStatus()
    {
        await WithController("abc needle abc", async (controller, navigator, statuses) =>
        {
            await controller.FindAsync("needle", direction: 1);
            controller.StopSearch();

            Assert.Null(navigator.HighlightTerms[^1]);
            Assert.Null(statuses[^1]);

            // A find after stop starts over from the first match.
            await controller.FindAsync("needle", direction: 1);
            Assert.Equal(navigator.Revealed[0], navigator.Revealed[^1]);
        });
    }

    [Fact]
    public async Task FindAfterDetach_IsIgnored()
    {
        await WithController("needle", async (controller, navigator, statuses) =>
        {
            controller.Detach();
            await controller.FindAsync("needle", direction: 1);
            Assert.Empty(navigator.Revealed);
        });
    }
}
