using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Progress;
using Argonaut.Features.Raw;

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

    internal readonly record struct NaiveRow(long Start, long End, bool SoftWrap, int? LineNumber);

    /// <summary>
    /// Independent dense implementation of the segmentation and numbering rules. Forced breaks
    /// are cap-anchored: the caps of a line sit at lineStart + k·W, a break is its cap backed off
    /// over up to 3 trailing continuation bytes, and the next cap is W past the last one however
    /// far that break backed off.
    /// </summary>
    internal static List<NaiveRow> NaiveScan(byte[] bytes, int wrapWidth)
    {
        static bool IsContinuation(byte b) => (b & 0xC0) == 0x80;

        var rows = new List<NaiveRow>();
        long start = 0;
        long cap = wrapWidth;
        int line = 1;
        bool atLineStart = true;
        while (start < bytes.Length)
        {
            long end;
            bool soft;
            long limit = Math.Min(cap, bytes.Length);
            int newline = Array.IndexOf(bytes, (byte)'\n', (int)start, (int)(limit - start));
            if (newline >= 0)
            {
                end = newline + 1;
                soft = false;
            }
            else if (cap >= bytes.Length)
            {
                // File ends before (or exactly at) the cap: a real end, no wrap marker.
                end = bytes.Length;
                soft = false;
            }
            else if (bytes[cap] == (byte)'\n')
            {
                end = cap + 1; // newline peek-extension
                soft = false;
            }
            else
            {
                int back = 0;
                while (back < 3 && IsContinuation(bytes[cap - back]))
                    back++;
                if (back == 3 && IsContinuation(bytes[cap - 3]))
                    back = 0; // four continuation bytes: not UTF-8, break at the cap
                end = cap - back;
                soft = true;
            }

            rows.Add(new NaiveRow(start, end, soft, atLineStart ? line : null));
            if (soft)
            {
                atLineStart = false;
                cap += wrapWidth;
            }
            else
            {
                line++;
                atLineStart = true;
                cap = end + wrapWidth;
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
            Assert.True(index.AllItemsPublished);
            Assert.Equal(expected.Count, index.RowCount);
            for (int i = 0; i < expected.Count; i++)
            {
                var info = index.GetRowInfo(i);
                Assert.Equal(expected[i].Start, info.Start);
                Assert.Equal(expected[i].End, info.End);
                Assert.Equal(expected[i].SoftWrap, info.IsSoftWrapped);
                Assert.Equal(expected[i].LineNumber, info.LineNumber);
                Assert.InRange(info.End - info.Start, 1, wrapWidth + RawRowBoundary.MaxUtf8Backoff + 1);
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
            Assert.True(index.AllItemsPublished);
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

            Assert.True(index.AllItemsPublished);
            Assert.Null(index.Failure); // cancellation is never reported as a failure
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

    /// <summary>
    /// The line a row sits in, which <see cref="RawRowInfo.LineNumber"/> deliberately does not
    /// report on a continuation row - a wrapped line should leave the gutter blank, but the caret
    /// readout still has to say "Ln 54". The line falls out of the anchor walk either way, so
    /// nothing is stored for it; this checks the two agree wherever both have an answer, and that
    /// a continuation row inherits the line of the row that started it.
    /// </summary>
    [Fact]
    public void LineContaining_ReportsTheLineForContinuationRowsToo()
    {
        // Lines of 200 x's at wrap 80: every line is one start row plus two continuation rows.
        var content = new StringBuilder();
        for (int line = 0; line < LinesBeyondOneAnchorBucket; line++)
            content.Append(new string('x', 200)).Append('\n');

        WithIndex(Encoding.UTF8.GetBytes(content.ToString()), 80, (index, _) =>
        {
            int expectedLine = 0;
            for (int row = 0; row < index.RowCount; row++)
            {
                var info = index.GetRowInfo(row);
                if (info.LineNumber is int startsLine)
                    expectedLine = startsLine;

                Assert.Equal(expectedLine, index.LineContaining(row));
            }

            Assert.Equal(LinesBeyondOneAnchorBucket, expectedLine);
            Assert.Null(index.LineContaining(index.RowCount));
        });
    }

    // ---- line queries ---------------------------------------------------------------------

    /// <summary>
    /// <see cref="RawSegmentIndex.LineStartContaining"/> and <see cref="RawSegmentIndex.LineEndContaining"/>
    /// against the naive scan, for every offset and one past the end - so a line that begins or
    /// ends exactly on a bucket edge, one that spans many buckets, and both kinds of end of data
    /// all get asked.
    /// </summary>
    private static void AssertLineQueriesMatchNaiveScan(byte[] content, int wrapWidth)
    {
        var rows = NaiveScan(content, wrapWidth);
        var index = RawSegmentIndex.StartIndexing(new MemoryByteSource(content), wrapWidth);
        index.IndexingTask.GetAwaiter().GetResult();

        // Per row: the row that starts its line, and the row that ends it.
        var lineFirst = new int[rows.Count];
        var lineLast = new int[rows.Count];
        for (int row = 0; row < rows.Count; row++)
            lineFirst[row] = rows[row].LineNumber is null ? lineFirst[row - 1] : row;
        for (int row = rows.Count - 1; row >= 0; row--)
            lineLast[row] = rows[row].SoftWrap ? lineLast[row + 1] : row;

        int rowAt = 0;
        for (long offset = 0; offset < content.Length; offset++)
        {
            while (rows[rowAt].End <= offset)
                rowAt++;

            var first = rows[lineFirst[rowAt]];
            var lastRow = rows[lineLast[rowAt]];
            int line = first.LineNumber!.Value;
            Assert.Equal((first.Start, lineFirst[rowAt], line), index.LineStartContaining(offset));
            Assert.Equal((lastRow.End, content[lastRow.End - 1] == (byte)'\n', lineLast[rowAt] + 1, line),
                index.LineEndContaining(offset));
        }

        // One past the end: the phantom line after a trailing newline, else the last line.
        int lines = rows.Count(r => r.LineNumber is not null);
        if (content.Length == 0 || content[^1] == (byte)'\n')
        {
            Assert.Equal((content.Length, rows.Count, lines + 1), index.LineStartContaining(content.Length));
            Assert.Equal((content.Length, false, rows.Count, lines + 1), index.LineEndContaining(content.Length));
        }
        else
        {
            Assert.Equal(lines, index.LineStartContaining(content.Length).LineNumber);
            Assert.Equal((content.Length, false, rows.Count, lines), index.LineEndContaining(content.Length));
        }
    }

    [Fact]
    public void LineQueries_ShortLinesAcrossBucketEdges()
    {
        var text = new StringBuilder();
        for (int i = 0; i < 400; i++)
            text.Append(new string('x', i % 23)).Append('\n');

        AssertLineQueriesMatchNaiveScan(Encoding.UTF8.GetBytes(text.ToString()), 8);
    }

    [Fact]
    public void LineQueries_ALongLineSpanningManyBuckets()
    {
        // ~500 rows at W=8 for the long line: the start and the end are both several buckets from
        // most of its offsets, so both binary-search paths run.
        var text = new StringBuilder("short\nlines\nfirst\n");
        for (int i = 0; i < 400; i++)
            text.Append(i % 3 == 0 ? "é日" : "abcd");
        text.Append("\nand\nafter\n");

        AssertLineQueriesMatchNaiveScan(Encoding.UTF8.GetBytes(text.ToString()), 8);
    }

    [Fact]
    public void LineQueries_AtTheEndOfData()
    {
        AssertLineQueriesMatchNaiveScan("one\ntwo\n"u8.ToArray(), 8);
        AssertLineQueriesMatchNaiveScan("one\ntwo, unterminated and wrapping"u8.ToArray(), 8);
        AssertLineQueriesMatchNaiveScan(Encoding.UTF8.GetBytes(new string('z', 3000)), 8);
    }

    [Fact]
    public void LineQueries_OnAnEmptySource()
    {
        var index = RawSegmentIndex.StartIndexing(new MemoryByteSource([]), 8);
        index.IndexingTask.GetAwaiter().GetResult();

        Assert.Equal((0L, 0, 1), index.LineStartContaining(0));
        Assert.Equal((0L, false, 0, 1), index.LineEndContaining(0));
    }
}
