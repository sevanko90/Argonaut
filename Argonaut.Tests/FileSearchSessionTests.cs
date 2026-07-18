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
public class FileSearchSessionTests
{
    private static void WithSession(string content, string term, int chunkSize,
        Action<FileSearchSession> assert, int maxMatches = 1_000_000)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
            using var file = new MMapFile(path);
            var session = FileSearchSession.Start(file, new LiteralSearchMatcher(term),
                chunkSize: chunkSize, maxMatches: maxMatches);
            session.ScanTask.GetAwaiter().GetResult();
            assert(session);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static long[] Offsets(FileSearchSession session)
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
            using var file = new MMapFile(path);
            var session = FileSearchSession.Start(file, new LiteralSearchMatcher("absent"));

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
    /// Blocks the scan inside its first window until released, making the cancellation
    /// race deterministic: Cancel() lands while the scan is provably still running.
    /// </summary>
    private sealed class BlockingMatcher : ISearchMatcher
    {
        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);

        public int WindowOverlap => 0;

        public bool TryFindNext(ReadOnlySpan<byte> window, int from, out int matchIndex, out int matchLength)
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
            using var file = new MMapFile(path);
            var matcher = new BlockingMatcher();
            var session = FileSearchSession.Start(file, matcher, chunkSize: 64);

            matcher.Entered.Wait();
            session.Cancel();
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
}
