using System.Text;
using JsonViewerCore.Features.NdJson;
using JsonViewerCore.Infrastructure;

namespace JsonViewerCore.Tests;

/// <summary>
/// Verifies the NDJSON line indexer against an independent naive byte scan: span
/// offsets/lengths, trailing-newline handling, CRLF, empty lines, lines that straddle or
/// end exactly on the indexer's internal scan-chunk boundaries, and text round-tripping
/// through <see cref="NdJsonLineReader"/>.
/// </summary>
public class FileOffsetIndexTests
{
    // Must exceed FileOffsetIndex.ScanChunkSize (4MB) so boundary tests actually cross it.
    private const int BeyondScanChunk = 5 * 1024 * 1024;
    private const int ScanChunk = 4 * 1024 * 1024;

    private static void WithIndex(byte[] content, Action<FileOffsetIndex, MMapFile> assert)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, content);
            using var file = new MMapFile(path);
            var index = FileOffsetIndex.StartIndexing(file);
            index.IndexingTask.GetAwaiter().GetResult();
            assert(index, file);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Independent single-pass byte scan producing the expected spans.</summary>
    private static List<FileLineSpan> NaiveScan(byte[] bytes)
    {
        var spans = new List<FileLineSpan>();
        long start = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                spans.Add(new FileLineSpan(start, (int)(i + 1 - start)));
                start = i + 1;
            }
        }

        if (start < bytes.Length)
            spans.Add(new FileLineSpan(start, (int)(bytes.Length - start)));

        return spans;
    }

    private static void AssertMatchesNaiveScan(byte[] content)
    {
        WithIndex(content, (index, _) =>
        {
            var expected = NaiveScan(content);
            Assert.True(index.IsComplete);
            Assert.Equal(expected.Count, index.LineCount);
            for (int i = 0; i < expected.Count; i++)
                Assert.Equal(expected[i], index.GetLineSpan(i));
        });
    }

    [Fact]
    public void SimpleLines_MatchExpectedSpans()
    {
        WithIndex("one\ntwo\nthree\n"u8.ToArray(), (index, _) =>
        {
            Assert.Equal(3, index.LineCount);
            Assert.Equal(new FileLineSpan(0, 4), index.GetLineSpan(0));
            Assert.Equal(new FileLineSpan(4, 4), index.GetLineSpan(1));
            Assert.Equal(new FileLineSpan(8, 6), index.GetLineSpan(2));
        });
    }

    [Fact]
    public void NoTrailingNewline_LastLineIsStillIndexed()
    {
        WithIndex("one\ntwo\nthree"u8.ToArray(), (index, _) =>
        {
            Assert.Equal(3, index.LineCount);
            Assert.Equal(new FileLineSpan(8, 5), index.GetLineSpan(2));
        });
    }

    [Fact]
    public void TrailingNewline_ProducesNoPhantomEmptyLine()
    {
        WithIndex("a\nb\n"u8.ToArray(), (index, _) => Assert.Equal(2, index.LineCount));
    }

    [Fact]
    public void EmptyLines_AreIndexedAsNewlineOnlySpans()
    {
        WithIndex("a\n\n\nb\n"u8.ToArray(), (index, _) =>
        {
            Assert.Equal(4, index.LineCount);
            Assert.Equal(new FileLineSpan(2, 1), index.GetLineSpan(1));
            Assert.Equal(new FileLineSpan(3, 1), index.GetLineSpan(2));
        });
    }

    [Fact]
    public void CrLfLineEndings_SpansIncludeBothTerminatorBytes()
    {
        WithIndex("one\r\ntwo\r\n"u8.ToArray(), (index, file) =>
        {
            Assert.Equal(2, index.LineCount);
            Assert.Equal(new FileLineSpan(0, 5), index.GetLineSpan(0));
            Assert.Equal(new FileLineSpan(5, 5), index.GetLineSpan(1));
            Assert.Equal("one", NdJsonLineReader.ReadLine(file, index.GetLineSpan(0)));
            Assert.Equal("two", NdJsonLineReader.ReadLine(file, index.GetLineSpan(1)));
        });
    }

    [Fact]
    public void GeneratedFile_SpansAreContiguousAndCoverWholeFile()
    {
        // Deterministic pseudo-random line lengths, some zero (consecutive newlines).
        var rng = new Random(12345);
        var sb = new StringBuilder();
        for (int i = 0; i < 500; i++)
        {
            sb.Append('x', rng.Next(0, 200));
            sb.Append('\n');
        }
        sb.Append("last line without newline");
        byte[] content = Encoding.UTF8.GetBytes(sb.ToString());

        AssertMatchesNaiveScan(content);

        WithIndex(content, (index, _) =>
        {
            long expectedStart = 0;
            for (int i = 0; i < index.LineCount; i++)
            {
                var span = index.GetLineSpan(i);
                Assert.Equal(expectedStart, span.Offset);
                Assert.True(span.Length > 0);
                expectedStart += span.Length;
            }

            Assert.Equal(content.Length, expectedStart);
        });
    }

    [Fact]
    public void LineLongerThanScanChunk_IsIndexedCorrectly()
    {
        byte[] content = new byte[BeyondScanChunk + 6];
        Array.Fill(content, (byte)'x');
        content[BeyondScanChunk] = (byte)'\n';
        content[^1] = (byte)'\n'; // second line: "xxxxx\n"

        AssertMatchesNaiveScan(content);
    }

    [Fact]
    public void NewlinesAtScanChunkBoundary_SplitCorrectly()
    {
        // Newlines at the last byte of the first scan chunk and the first byte of the
        // second, so line accounting must carry state across the chunk seam.
        byte[] content = new byte[ScanChunk + 32];
        Array.Fill(content, (byte)'x');
        content[ScanChunk - 1] = (byte)'\n';
        content[ScanChunk] = (byte)'\n';
        content[^1] = (byte)'\n';

        AssertMatchesNaiveScan(content);
    }

    [Fact]
    public void EmptyFile_CompletesWithZeroLines()
    {
        WithIndex(Array.Empty<byte>(), (index, _) =>
        {
            Assert.True(index.IsComplete);
            Assert.Equal(0, index.LineCount);
        });
    }

    [Fact]
    public void WaitForLineCountAsync_CompletesForReachableAndUnreachableTargets()
    {
        WithIndex("a\nb\nc\n"u8.ToArray(), (index, _) =>
        {
            // Reachable target.
            Assert.True(index.WaitForLineCountAsync(2).Wait(TimeSpan.FromSeconds(5)));
            // Target beyond the file's line count must still complete once indexing finishes.
            Assert.True(index.WaitForLineCountAsync(1000).Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(3, index.LineCount);
        });
    }

    [Fact]
    public void MultibyteUtf8Lines_RoundTripThroughLineReader()
    {
        string[] lines = ["héllo wörld", "日本語のテキスト", "emoji: 🚀🎉", "plain ascii"];
        byte[] content = Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");

        WithIndex(content, (index, file) =>
        {
            Assert.Equal(lines.Length, index.LineCount);
            for (int i = 0; i < lines.Length; i++)
                Assert.Equal(lines[i], NdJsonLineReader.ReadLine(file, index.GetLineSpan(i)));
        });
    }
}
