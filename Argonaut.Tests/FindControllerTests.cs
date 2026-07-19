using System.Text;
using Argonaut.Features.Search;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Exercises the find orchestration against real temp files and scans: cursor stepping and
/// wrap-around, backward stepping, no-match status, term changes superseding the previous
/// session, stop cancelling the scan before the file could be disposed, and the reveal
/// hand-off to the navigator. FindController itself is UI-framework-free (delegates +
/// ISearchNavigator), so these run as plain unit tests; per the app convention its awaits
/// just resume on the caller's context.
/// </summary>
public class FindControllerTests
{
    private sealed class StubNavigator(MMapFile file) : ISearchNavigator
    {
        public MMapFile File { get; } = file;
        public List<string?> HighlightTerms { get; } = new();
        public List<SearchMatch> Revealed { get; } = new();

        public void SetHighlightTerm(string? term) => HighlightTerms.Add(term);

        public Task RevealAsync(SearchMatch match, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Revealed.Add(match);
            return Task.CompletedTask;
        }
    }

    private static async Task WithController(string content,
        Func<FindController, StubNavigator, List<string?>, Task> test)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
            using var file = new MMapFile(path);
            var navigator = new StubNavigator(file);
            var statuses = new List<string?>();
            var controller = new FindController(statuses.Add, () => null);
            controller.Attach(navigator);

            await test(controller, navigator, statuses);

            // Tests must leave no scan running over the file we're about to dispose.
            await controller.DetachAsync();
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
    public async Task StopAsync_ClearsHighlightAndStatus()
    {
        await WithController("abc needle abc", async (controller, navigator, statuses) =>
        {
            await controller.FindAsync("needle", direction: 1);
            await controller.StopAsync();

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
            await controller.DetachAsync();
            await controller.FindAsync("needle", direction: 1);
            Assert.Empty(navigator.Revealed);
        });
    }
}
