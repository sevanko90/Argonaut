using System.Text;
using Argonaut.Features.Raw;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Verifies the raw viewer's sparse segment indexer against an independent naive scan
/// implementing the same rules (break at '\n' or at the wrap cap, newline peek at the cap,
/// UTF-8 backoff): newline semantics match the NDJSON indexer, forced breaks land where they
/// should with the soft-wrap flag set, rows stay contiguous and cap-bounded, line numbering
/// marks only the first row of each real line - and, since only every 64th row is anchored,
/// that rows re-derived from an anchor walk agree with the dense reference everywhere.
/// </summary>
public class RawSegmentIndexTests
{
    private const int LinesBeyondOneAnchorBucket = 200; // > 3 anchor strides of 64

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

    private readonly record struct NaiveRow(long Start, long End, bool SoftWrap, int? LineNumber);

    /// <summary>Independent dense implementation of the segmentation and numbering rules.</summary>
    private static List<NaiveRow> NaiveScan(byte[] bytes, int wrapWidth)
    {
        var rows = new List<NaiveRow>();
        long start = 0;
        int line = 1;
        bool atLineStart = true;
        while (start < bytes.Length)
        {
            long end;
            bool soft;
            long limit = Math.Min(start + wrapWidth, bytes.Length);
            int newline = Array.IndexOf(bytes, (byte)'\n', (int)start, (int)(limit - start));
            if (newline >= 0)
            {
                end = newline + 1;
                soft = false;
            }
            else if (start + wrapWidth >= bytes.Length)
            {
                // File ends before (or exactly at) the cap: a real end, no wrap marker.
                end = bytes.Length;
                soft = false;
            }
            else
            {
                long capEnd = start + wrapWidth;
                if (bytes[capEnd] == (byte)'\n')
                {
                    end = capEnd + 1; // newline peek-extension
                    soft = false;
                }
                else
                {
                    end = capEnd;
                    for (int back = 0; back < 3 && end - 1 > start; back++)
                    {
                        if ((bytes[end] & 0xC0) != 0x80)
                            break;
                        end--;
                    }
                    if ((bytes[end] & 0xC0) == 0x80)
                        end = capEnd;
                    soft = true;
                }
            }

            rows.Add(new NaiveRow(start, end, soft, atLineStart ? line : null));
            if (soft)
            {
                atLineStart = false;
            }
            else
            {
                line++;
                atLineStart = true;
            }

            start = end;
        }

        return rows;
    }

    private static void AssertMatchesNaiveScan(byte[] content, int wrapWidth)
    {
        WithIndex(content, wrapWidth, (index, _) =>
        {
            var expected = NaiveScan(content, wrapWidth);
            Assert.True(index.IsComplete);
            Assert.Equal(expected.Count, index.RowCount);
            for (int i = 0; i < expected.Count; i++)
            {
                var info = index.GetRowInfo(i);
                Assert.Equal(expected[i].Start, info.Start);
                Assert.Equal(expected[i].End, info.End);
                Assert.Equal(expected[i].SoftWrap, info.IsSoftWrapped);
                Assert.Equal(expected[i].LineNumber, info.LineNumber);
                Assert.InRange(info.End - info.Start, 1, wrapWidth + 1);
            }
        });
    }

    [Fact]
    public void SimpleLines_MatchNdjsonLineSemantics()
    {
        WithIndex("one\ntwo\nthree\n"u8.ToArray(), 80, (index, _) =>
        {
            Assert.Equal(3, index.RowCount);
            Assert.Equal(new RawRowInfo(0, 4, false, 1), index.GetRowInfo(0));
            Assert.Equal(new RawRowInfo(4, 8, false, 2), index.GetRowInfo(1));
            Assert.Equal(new RawRowInfo(8, 14, false, 3), index.GetRowInfo(2));
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
            Assert.Equal(3, index.RowCount);
            // One real line: number on its first row only, continuations blank.
            Assert.Equal(new RawRowInfo(0, 40, true, 1), index.GetRowInfo(0));
            Assert.Equal(new RawRowInfo(40, 80, true, null), index.GetRowInfo(1));
            Assert.Equal(new RawRowInfo(80, 101, false, null), index.GetRowInfo(2));
        });
    }

    [Fact]
    public void CapExactlyAtEndOfFile_IsARealEndNotASoftWrap()
    {
        var content = new byte[40];
        Array.Fill(content, (byte)'x');

        WithIndex(content, 40, (index, _) =>
        {
            Assert.Equal(1, index.RowCount);
            Assert.Equal(new RawRowInfo(0, 40, false, 1), index.GetRowInfo(0));
        });
    }

    [Fact]
    public void OneByteOverCap_ProducesWrappedRowThenRemainder()
    {
        var content = new byte[41];
        Array.Fill(content, (byte)'x');

        WithIndex(content, 40, (index, _) =>
        {
            Assert.Equal(2, index.RowCount);
            Assert.Equal(new RawRowInfo(0, 40, true, 1), index.GetRowInfo(0));
            Assert.Equal(new RawRowInfo(40, 41, false, null), index.GetRowInfo(1));
        });
    }

    [Fact]
    public void NewlineExactlyAtCap_IsPeekExtendedIntoARealEnd()
    {
        // 40 x's, then '\n' at the cap, then a short second line.
        byte[] content = Encoding.ASCII.GetBytes(new string('x', 40) + "\nb\n");

        WithIndex(content, 40, (index, _) =>
        {
            Assert.Equal(2, index.RowCount);
            Assert.Equal(new RawRowInfo(0, 41, false, 1), index.GetRowInfo(0)); // real newline, no ⏎ marker
            Assert.Equal(2, index.GetRowInfo(1).LineNumber);
        });
    }

    [Fact]
    public void CrLfStraddlingTheCap_DoesNotLeaveALoneLinefeedRow()
    {
        // 39 x's + "\r\n": the '\r' is byte 39 (the last inside the cap), the '\n' is byte 40.
        byte[] content = Encoding.ASCII.GetBytes(new string('x', 39) + "\r\nsecond\n");

        WithIndex(content, 40, (index, file) =>
        {
            Assert.Equal(2, index.RowCount);
            Assert.Equal(new RawRowInfo(0, 41, false, 1), index.GetRowInfo(0));
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
            var row0 = index.GetRowInfo(0);
            Assert.Equal(38, row0.End); // backed off to before the lead byte
            Assert.True(row0.IsSoftWrapped);
            // The next row starts on the char boundary, so the text decodes cleanly.
            var row1 = index.GetRowInfo(1);
            string text1 = RawRowReader.ReadRow(file, row1.Start, row1.End, row1.IsSoftWrapped);
            Assert.StartsWith("日", text1);
            Assert.DoesNotContain('�', text1);
        });

        AssertMatchesNaiveScan(content, 40);
    }

    [Fact]
    public void PureContinuationBytes_BreakAtTheCapRegardless()
    {
        // Invalid UTF-8 (endless 0x80 continuation bytes): backoff finds no boundary and
        // must give up at the cap, not walk backwards forever or emit short rows.
        var content = new byte[100];
        Array.Fill(content, (byte)0x80);

        WithIndex(content, 40, (index, _) =>
        {
            Assert.Equal(3, index.RowCount);
            Assert.Equal(new RawRowInfo(0, 40, true, 1), index.GetRowInfo(0));
            Assert.Equal(new RawRowInfo(40, 80, true, null), index.GetRowInfo(1));
            Assert.Equal(new RawRowInfo(80, 100, false, null), index.GetRowInfo(2));
        });
    }

    [Fact]
    public void NewlinelessFile_NumbersOnlyTheFirstRow()
    {
        var content = new byte[200];
        Array.Fill(content, (byte)'x');

        WithIndex(content, 80, (index, _) =>
        {
            Assert.Equal(3, index.RowCount);
            Assert.Equal(1, index.GetRowInfo(0).LineNumber);
            Assert.Null(index.GetRowInfo(1).LineNumber);
            Assert.Null(index.GetRowInfo(2).LineNumber);
        });
    }

    [Fact]
    public void RowsAcrossAnchorBuckets_RederiveFromTheirOwnAnchor()
    {
        // 200 one-row lines: rows past index 63 live in later anchor buckets, so their
        // offsets and line numbers come from an anchor walk, not from row 0.
        var sb = new StringBuilder();
        for (int i = 0; i < LinesBeyondOneAnchorBucket; i++)
            sb.Append($"row {i:D3}\n");
        byte[] content = Encoding.ASCII.GetBytes(sb.ToString());

        WithIndex(content, 80, (index, file) =>
        {
            Assert.Equal(LinesBeyondOneAnchorBucket, index.RowCount);
            foreach (int r in new[] { 0, 63, 64, 65, 127, 128, 199 })
            {
                var info = index.GetRowInfo(r);
                Assert.Equal(r + 1, info.LineNumber);
                Assert.False(info.IsSoftWrapped);
                Assert.Equal($"row {r:D3}", RawRowReader.ReadRow(file, info.Start, info.End, info.IsSoftWrapped));
            }
        });
    }

    [Fact]
    public void GeneratedMixedFile_MatchesNaiveScanEverywhere()
    {
        // Several MB of deterministic pseudo-random content: newline-terminated lines of
        // varied length (many beyond the cap), occasional CRLF, and multibyte runs - so
        // thousands of anchor buckets exist and every break rule gets exercised, including
        // rows re-derived mid-bucket.
        var rng = new Random(12345);
        var sb = new StringBuilder();
        while (sb.Length < 5 * 1024 * 1024)
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
    public void EmptyFile_CompletesWithZeroRows()
    {
        WithIndex(Array.Empty<byte>(), 80, (index, _) =>
        {
            Assert.True(index.IsComplete);
            Assert.Equal(0, index.RowCount);
        });
    }

    [Fact]
    public void WaitForRowCountAsync_CompletesForReachableAndUnreachableTargets()
    {
        WithIndex("a\nb\nc\n"u8.ToArray(), 80, (index, _) =>
        {
            Assert.True(index.WaitForRowCountAsync(2).Wait(TimeSpan.FromSeconds(5)));
            Assert.True(index.WaitForRowCountAsync(1000).Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(3, index.RowCount);
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

    /// <summary>Cancels the scan after the first progress report fires.</summary>
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
    public async Task CancelledMidScan_PublishesOnlyScannedCapBoundedRows()
    {
        // Cancelling partway through must leave a consistent partial index: every published
        // row cap-bounded and resolvable, none covering the un-scanned remainder.
        var content = new byte[12 * 1024 * 1024];
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
            Assert.True(index.RowCount > 0);
            Assert.True(index.RowCount < content.Length / 512, "cancellation should leave the tail un-indexed");
            for (int i = 0; i < index.RowCount; i++)
            {
                var info = index.GetRowInfo(i);
                Assert.InRange(info.End - info.Start, 1, 513);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
