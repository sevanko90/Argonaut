using System.Text;
using Argonaut.Features.Search;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Verifies the background file scan: window/overlap handling (a match straddling a chunk
/// boundary is found exactly once, overlap bytes never re-emit a match), non-overlapping
/// match semantics, terms longer than the chunk size, the match cap, waiter completion when
/// the scan ends short of the target, and cooperative cancellation.
/// </summary>
public class SearchSessionTests
{
    private static void WithSession(string content, string term, int chunkSize,
        Action<SearchSession> assert, int maxMatches = 1_000_000)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
            var session = SearchSession.Start(LoadFromPath.ScanTargetFor(path), new LiteralSearchMatcher(term),
                chunkSize: chunkSize, maxMatches: maxMatches);
            session.ScanTask.GetAwaiter().GetResult();
            assert(session);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static long[] Offsets(SearchSession session)
    {
        var offsets = new long[session.MatchCount];
        for (int i = 0; i < offsets.Length; i++)
            offsets[i] = session.GetMatch(i).Offset;
        return offsets;
    }

    [Fact]
    public void MatchStraddlingChunkBoundary_FoundExactlyOnce()
    {
        // "needle" spans the byte-32 chunk boundary (starts at 30, chunk size 32).
        string content = new string('x', 30) + "needle" + new string('x', 12);

        WithSession(content, "needle", chunkSize: 32, session =>
        {
            Assert.True(session.IsComplete);
            Assert.Equal(new long[] { 30 }, Offsets(session));
        });
    }

    [Fact]
    public void RepeatedPattern_TinyChunks_NonOverlappingMatchesFoundOnceEach()
    {
        // Every window boundary lands inside some occurrence; the dedup cursor must
        // neither miss nor double-count any of them, and "aa"-style self-overlap must
        // follow editor semantics (non-overlapping).
        string content = string.Concat(Enumerable.Repeat("ab", 64)); // 128 bytes

        WithSession(content, "ab", chunkSize: 8, session =>
        {
            Assert.Equal(64, session.MatchCount);
            Assert.Equal(Enumerable.Range(0, 64).Select(i => (long)(i * 2)), Offsets(session));
        });
    }

    [Fact]
    public void SelfOverlappingTerm_MatchesAreNonOverlapping()
    {
        WithSession("aaaa", "aa", chunkSize: 32, session =>
        {
            Assert.Equal(new long[] { 0, 2 }, Offsets(session));
        });
    }

    [Fact]
    public void TermLongerThanChunkSize_StillFound()
    {
        WithSession("xxabcdefghijxx", "abcdefghij", chunkSize: 4, session =>
        {
            Assert.Equal(new long[] { 2 }, Offsets(session));
        });
    }

    [Fact]
    public void EmptyFile_CompletesWithNoMatches()
    {
        WithSession(string.Empty, "anything", chunkSize: 32, session =>
        {
            Assert.True(session.IsComplete);
            Assert.Equal(0, session.MatchCount);
            Assert.False(session.WasCancelled);
        });
    }

    [Fact]
    public void MatchCap_StopsScanAndSetsFlag()
    {
        string content = string.Concat(Enumerable.Repeat("ab", 100));

        WithSession(content, "ab", chunkSize: 1024, session =>
        {
            Assert.True(session.HitMatchCap);
            Assert.True(session.IsComplete);
            Assert.Equal(10, session.MatchCount);
        }, maxMatches: 10);
    }

    [Fact]
    public void CaseInsensitive_EndToEnd()
    {
        WithSession("say Hello and hELLo", "hello", chunkSize: 32, session =>
        {
            Assert.Equal(new long[] { 4, 14 }, Offsets(session));
        });
    }

    [Fact]
    public async Task WaitForMatchCount_CompletesWhenScanEndsShortOfTarget()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("no hits here"));
            var session = SearchSession.Start(LoadFromPath.ScanTargetFor(path), new LiteralSearchMatcher("absent"));

            await session.WaitForMatchCountAsync(5);

            Assert.True(session.IsComplete);
            Assert.Equal(0, session.MatchCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Blocks the scan inside its first chunk until released, making the cancellation
    /// race deterministic: Cancel() lands while the scan is provably still running.
    /// </summary>
    private sealed class BlockingMatcher : ISearchMatcher
    {
        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);

        public int ChunkOverlap => 0;

        public bool TryFindNext(ReadOnlySpan<byte> chunk, int from, out int matchIndex, out int matchLength)
        {
            Entered.Set();
            Release.Wait();
            matchIndex = -1;
            matchLength = 0;
            return false;
        }
    }

    [Fact]
    public async Task Cancel_MidScan_CompletesNonFaultedWithWasCancelled()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[256]);
            var matcher = new BlockingMatcher();
            var session = SearchSession.Start(LoadFromPath.ScanTargetFor(path), matcher, chunkSize: 64);

            matcher.Entered.Wait();
            session.RequestStop();
            matcher.Release.Set();

            await session.ScanTask; // must not throw
            Assert.True(session.IsComplete);
            Assert.True(session.WasCancelled);

            // A waiter registered against a cancelled scan must still be released.
            await session.WaitForMatchCountAsync(1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The scan opens its own mapping per chunk, so it must let go of the file when it stops -
    /// deleting the file afterwards is the observable proof. On Windows a still-mapped file
    /// cannot be deleted at all; elsewhere the unlink would succeed regardless, so the
    /// assertion only bites on Windows and is harmless on the other platforms CI runs.
    /// </summary>
    [Fact]
    public async Task Scan_ReleasesItsMapping_FileDeletableAfterCompletion()
    {
        string path = Path.GetTempFileName();
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("abc needle abc"));

        var session = SearchSession.Start(LoadFromPath.ScanTargetFor(path), new LiteralSearchMatcher("needle"));
        await session.ScanTask;

        Assert.Equal(1, session.MatchCount);
        File.Delete(path); // throws if the scan is still holding a mapping
        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// The scan - not the caller - opens the file, so an unreadable target is a scan OUTCOME
    /// rather than a throw out of Start: ScanTask must complete unfaulted (nothing joins it any
    /// more, so a fault would surface later as an UnobservedTaskException) and say why.
    /// </summary>
    [Fact]
    public async Task Scan_MissingPath_CompletesWithOpenFailureAndDoesNotFault()
    {
        string path = Path.Combine(Path.GetTempPath(), $"argonaut-missing-{Guid.NewGuid():N}.json");

        var session = SearchSession.Start(LoadFromPath.ScanTargetFor(path), new LiteralSearchMatcher("needle"));
        await session.ScanTask; // must not throw

        Assert.True(session.ScanTask.IsCompletedSuccessfully);
        Assert.True(session.IsComplete);
        Assert.Equal(0, session.MatchCount);
        Assert.NotNull(session.OpenFailure);
    }

    /// <summary>
    /// A range target reports offsets relative to the range start, not absolute in the file -
    /// the property that lets a sub-document (one NDJSON line, whose index is zero-based at the
    /// line start) be searched in the coordinate system its own index speaks.
    /// </summary>
    [Fact]
    public async Task RangeTarget_ReportsOffsetsRelativeToTheRange()
    {
        string path = Path.GetTempFileName();
        try
        {
            // "needle" at absolute 4 (outside the range) and at absolute 24 (inside it).
            byte[] bytes = Encoding.UTF8.GetBytes("abc needle abc\nxyz needle xyz\n");
            File.WriteAllBytes(path, bytes);

            int lineStart = Encoding.UTF8.GetByteCount("abc needle abc\n");
            int lineLength = Encoding.UTF8.GetByteCount("xyz needle xyz");

            var session = SearchSession.Start(LoadFromPath.ScanTargetFor(path, lineStart, lineLength),
                new LiteralSearchMatcher("needle"));
            await session.ScanTask;

            Assert.Equal(1, session.MatchCount);            // the one outside the range is unseen
            Assert.Equal(4, session.GetMatch(0).Offset);    // relative to the line, not absolute
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Many sequential searches over one path must stay flat: each session's mappings are its
    /// own and are released as it goes, so nothing accumulates between runs.
    /// </summary>
    [Fact]
    public async Task ManySequentialSearches_CompletePromptly()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("no hits in here at all"));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 200; i++)
            {
                var session = SearchSession.Start(LoadFromPath.ScanTargetFor(path), new LiteralSearchMatcher("absent"));
                await session.ScanTask;
            }
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 5000,
                $"200 sequential searches took {sw.ElapsedMilliseconds}ms.");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
