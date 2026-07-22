using System.Text;
using Argonaut.Features.Raw;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Verifies the raw viewer's segment indexer against an independent naive scan implementing
/// the same rules (break at '\n' or at the wrap cap, newline peek at the cap, UTF-8 backoff):
/// newline semantics match the NDJSON indexer, forced breaks land where they should with the
/// soft-wrap flag set, segments stay contiguous and cap-bounded across scan-chunk seams, and
/// line numbering marks only the first segment of each real line.
/// </summary>
public class RawSegmentIndexTests
{
    // Must exceed RawSegmentIndex.ScanChunkSize (4MB) so boundary tests actually cross it.
    private const int BeyondScanChunk = 5 * 1024 * 1024;

    private static void WithIndex(byte[] content, int wrapWidth, Action<RawSegmentIndex, MMapFile> assert)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, content);
            using var file = new MMapFile(path);
            var index = RawSegmentIndex.StartIndexing(file, wrapWidth);
            index.IndexingTask.GetAwaiter().GetResult();
            assert(index, file);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Independent implementation of the segmentation rules producing (end, softWrap) pairs.</summary>
    private static List<(long End, bool SoftWrap)> NaiveScan(byte[] bytes, int wrapWidth)
    {
        var segments = new List<(long, bool)>();
        long start = 0;
        while (start < bytes.Length)
        {
            long limit = Math.Min(start + wrapWidth, bytes.Length);
            int newline = Array.IndexOf(bytes, (byte)'\n', (int)start, (int)(limit - start));
            if (newline >= 0)
            {
                segments.Add((newline + 1, false));
                start = newline + 1;
                continue;
            }

            if (start + wrapWidth >= bytes.Length)
            {
                // File ends before (or exactly at) the cap: a real end, no wrap marker.
                segments.Add((bytes.Length, false));
                break;
            }

            long capEnd = start + wrapWidth;
            if (bytes[capEnd] == (byte)'\n')
            {
                segments.Add((capEnd + 1, false)); // newline peek-extension
                start = capEnd + 1;
                continue;
            }

            long end = capEnd;
            for (int back = 0; back < 3 && end - 1 > start; back++)
            {
                if ((bytes[end] & 0xC0) != 0x80)
                    break;
                end--;
            }
            if ((bytes[end] & 0xC0) == 0x80)
                end = capEnd;

            segments.Add((end, true));
            start = end;
        }

        return segments;
    }

    private static void AssertMatchesNaiveScan(byte[] content, int wrapWidth)
    {
        WithIndex(content, wrapWidth, (index, _) =>
        {
            var expected = NaiveScan(content, wrapWidth);
            Assert.True(index.IsComplete);
            Assert.Equal(expected.Count, index.SegmentCount);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].End, index.GetSegmentEnd(i));
                Assert.Equal(expected[i].SoftWrap, index.IsSoftWrapped(i));
            }

            AssertStructuralInvariants(index, content.Length, wrapWidth);
        });
    }

    /// <summary>Contiguity, full coverage, cap-bounded lengths, and line-number consistency.</summary>
    private static void AssertStructuralInvariants(RawSegmentIndex index, long fileLength, int wrapWidth)
    {
        long expectedStart = 0;
        int expectedLineNumber = 1;
        bool atLineStart = true;
        for (int i = 0; i < index.SegmentCount; i++)
        {
            Assert.Equal(expectedStart, index.GetSegmentStart(i));
            long length = index.GetSegmentEnd(i) - index.GetSegmentStart(i);
            Assert.InRange(length, 1, wrapWidth + 1);

            Assert.Equal(atLineStart ? expectedLineNumber : null, index.GetLineNumber(i));
            if (atLineStart)
                expectedLineNumber++;
            atLineStart = !index.IsSoftWrapped(i);

            expectedStart = index.GetSegmentEnd(i);
        }

        Assert.Equal(fileLength, expectedStart);
    }

    [Fact]
    public void SimpleLines_MatchNdjsonLineSemantics()
    {
        WithIndex("one\ntwo\nthree\n"u8.ToArray(), 80, (index, _) =>
        {
            Assert.Equal(3, index.SegmentCount);
            Assert.Equal(4, index.GetSegmentEnd(0));
            Assert.Equal(8, index.GetSegmentEnd(1));
            Assert.Equal(14, index.GetSegmentEnd(2));
            Assert.False(index.IsSoftWrapped(0));
            Assert.False(index.IsSoftWrapped(1));
            Assert.False(index.IsSoftWrapped(2));
            Assert.Equal(1, index.GetLineNumber(0));
            Assert.Equal(2, index.GetLineNumber(1));
            Assert.Equal(3, index.GetLineNumber(2));
        });
    }

    [Fact]
    public void LongLine_IsForceBrokenAtCapMultiples()
    {
        var content = new byte[101];
        Array.Fill(content, (byte)'x');
        content[100] = (byte)'\n';

        WithIndex(content, 40, (index, _) =>
        {
            Assert.Equal(3, index.SegmentCount);
            Assert.Equal(40, index.GetSegmentEnd(0));
            Assert.True(index.IsSoftWrapped(0));
            Assert.Equal(80, index.GetSegmentEnd(1));
            Assert.True(index.IsSoftWrapped(1));
            Assert.Equal(101, index.GetSegmentEnd(2));
            Assert.False(index.IsSoftWrapped(2));

            // One real line: number on its first segment only, continuations blank.
            Assert.Equal(1, index.GetLineNumber(0));
            Assert.Null(index.GetLineNumber(1));
            Assert.Null(index.GetLineNumber(2));
        });
    }

    [Fact]
    public void CapExactlyAtEndOfFile_IsARealEndNotASoftWrap()
    {
        var content = new byte[40];
        Array.Fill(content, (byte)'x');

        WithIndex(content, 40, (index, _) =>
        {
            Assert.Equal(1, index.SegmentCount);
            Assert.Equal(40, index.GetSegmentEnd(0));
            Assert.False(index.IsSoftWrapped(0));
        });
    }

    [Fact]
    public void OneByteOverCap_ProducesWrappedSegmentThenRemainder()
    {
        var content = new byte[41];
        Array.Fill(content, (byte)'x');

        WithIndex(content, 40, (index, _) =>
        {
            Assert.Equal(2, index.SegmentCount);
            Assert.Equal(40, index.GetSegmentEnd(0));
            Assert.True(index.IsSoftWrapped(0));
            Assert.Equal(41, index.GetSegmentEnd(1));
            Assert.False(index.IsSoftWrapped(1));
        });
    }

    [Fact]
    public void NewlineExactlyAtCap_IsPeekExtendedIntoARealEnd()
    {
        // 40 x's, then '\n' at the cap, then a short second line.
        byte[] content = Encoding.ASCII.GetBytes(new string('x', 40) + "\nb\n");

        WithIndex(content, 40, (index, _) =>
        {
            Assert.Equal(2, index.SegmentCount);
            Assert.Equal(41, index.GetSegmentEnd(0));
            Assert.False(index.IsSoftWrapped(0)); // real newline, no ⏎ marker
            Assert.Equal(2, index.GetLineNumber(1));
        });
    }

    [Fact]
    public void CrLfStraddlingTheCap_DoesNotLeaveALoneLinefeedRow()
    {
        // 39 x's + "\r\n": the '\r' is byte 39 (the last inside the cap), the '\n' is byte 40.
        byte[] content = Encoding.ASCII.GetBytes(new string('x', 39) + "\r\nsecond\n");

        WithIndex(content, 40, (index, file) =>
        {
            Assert.Equal(2, index.SegmentCount);
            Assert.Equal(41, index.GetSegmentEnd(0));
            Assert.False(index.IsSoftWrapped(0));
            Assert.Equal(new string('x', 39), RawRowReader.ReadRow(file, 0, 41, false));
        });
    }

    [Fact]
    public void MultibyteCharStraddlingTheCap_BacksOffToTheCharBoundary()
    {
        // 38 ASCII bytes then "日" (3 bytes: E6 97 A5) - the cap at 40 lands mid-character.
        byte[] content = Encoding.UTF8.GetBytes(new string('x', 38) + "日日日");

        WithIndex(content, 40, (index, file) =>
        {
            Assert.Equal(38, index.GetSegmentEnd(0)); // backed off to before the lead byte
            Assert.True(index.IsSoftWrapped(0));
            // The next segment starts on the char boundary, so the text decodes cleanly.
            var row1 = RawRowReader.ReadRow(file, index.GetSegmentStart(1), index.GetSegmentEnd(1), index.IsSoftWrapped(1));
            Assert.StartsWith("日", row1);
            Assert.DoesNotContain('�', row1);
        });

        AssertMatchesNaiveScan(content, 40);
    }

    [Fact]
    public void PureContinuationBytes_BreakAtTheCapRegardless()
    {
        // Invalid UTF-8 (endless 0x80 continuation bytes): backoff finds no boundary and
        // must give up at the cap, not walk backwards forever or emit short segments.
        var content = new byte[100];
        Array.Fill(content, (byte)0x80);

        WithIndex(content, 40, (index, _) =>
        {
            Assert.Equal(3, index.SegmentCount);
            Assert.Equal(40, index.GetSegmentEnd(0));
            Assert.True(index.IsSoftWrapped(0));
            Assert.Equal(80, index.GetSegmentEnd(1));
            Assert.Equal(100, index.GetSegmentEnd(2));
        });
    }

    [Fact]
    public void NewlinelessFile_HasASingleLineStart()
    {
        var content = new byte[200];
        Array.Fill(content, (byte)'x');

        WithIndex(content, 80, (index, _) =>
        {
            Assert.Equal(3, index.SegmentCount);
            Assert.Equal(1, index.GetLineNumber(0));
            Assert.Null(index.GetLineNumber(1));
            Assert.Null(index.GetLineNumber(2));
        });
    }

    [Fact]
    public void GeneratedMixedFile_MatchesNaiveScanAcrossChunkSeams()
    {
        // > 4MB of deterministic pseudo-random content: newline-terminated lines of varied
        // length (many beyond the cap), occasional CRLF, and multibyte runs - so segments
        // straddle the internal scan-chunk boundary and every break rule gets exercised.
        var rng = new Random(12345);
        var sb = new StringBuilder();
        while (sb.Length < BeyondScanChunk)
        {
            int lineLength = rng.Next(0, 2000);
            sb.Append(rng.Next(4) == 0 ? new string('é', lineLength / 2) : new string('x', lineLength));
            sb.Append(rng.Next(8) == 0 ? "\r\n" : "\n");
        }
        sb.Append("last line without newline");
        byte[] content = Encoding.UTF8.GetBytes(sb.ToString());

        AssertMatchesNaiveScan(content, 512);
    }

    [Fact]
    public void EmptyFile_CompletesWithZeroSegments()
    {
        WithIndex(Array.Empty<byte>(), 80, (index, _) =>
        {
            Assert.True(index.IsComplete);
            Assert.Equal(0, index.SegmentCount);
        });
    }

    [Fact]
    public void WaitForSegmentCountAsync_CompletesForReachableAndUnreachableTargets()
    {
        WithIndex("a\nb\nc\n"u8.ToArray(), 80, (index, _) =>
        {
            Assert.True(index.WaitForSegmentCountAsync(2).Wait(TimeSpan.FromSeconds(5)));
            Assert.True(index.WaitForSegmentCountAsync(1000).Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(3, index.SegmentCount);
        });
    }

    [Fact]
    public void TinyWrapWidth_IsRejected()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, "abc"u8.ToArray());
            using var file = new MMapFile(path);
            Assert.Throws<ArgumentOutOfRangeException>(() => RawSegmentIndex.StartIndexing(file, 3));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Cancels the scan after the first chunk's progress report fires.</summary>
    private sealed class CancelAfterFirstReport : IProgressReporter
    {
        private readonly CancellationTokenSource cts;
        private bool cancelled;

        public CancelAfterFirstReport(CancellationTokenSource cts) => this.cts = cts;

        public void Report(string message, long? current = null, long? max = null)
        {
            if (cancelled)
                return;
            cancelled = true;
            cts.Cancel();
        }
    }

    [Fact]
    public async Task CancelledMidScan_DoesNotAppendBogusRemainderSegment()
    {
        // Multi-chunk file; cancelling after the first 4MB chunk must not record the whole
        // un-scanned remainder as one segment (see the same trap in FileOffsetIndexTests).
        var content = new byte[3 * 4 * 1024 * 1024];
        Array.Fill(content, (byte)'x');

        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, content);
            using var file = new MMapFile(path);
            var cts = new CancellationTokenSource();

            var index = RawSegmentIndex.StartIndexing(file, 512, new CancelAfterFirstReport(cts), cts.Token);
            try { await index.IndexingTask; }
            catch (OperationCanceledException) { /* expected clean cancellation */ }

            Assert.True(index.IsComplete);
            for (int i = 0; i < index.SegmentCount; i++)
            {
                long length = index.GetSegmentEnd(i) - index.GetSegmentStart(i);
                Assert.True(length <= 513, $"segment {i} has length {length}; a bogus remainder segment leaked in");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
