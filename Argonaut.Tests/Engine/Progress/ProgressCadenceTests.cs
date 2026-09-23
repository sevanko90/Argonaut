using System.Collections.Concurrent;
using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Lines;
using Argonaut.Engine.Progress;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Raw.Rows;

namespace Argonaut.Tests.Engine.Progress;

/// <summary>
/// Producers report how far they have got, at a cadence of their own; how finely that is shown
/// is the reporter's decision alone (see <see cref="IProgressReporter"/>). A producer that sized
/// its reporting to a fraction of the file - the JSON index used to report every twentieth -
/// would report many times over even a tiny file, so a small file reporting only its end is what
/// shows none of them does.
/// </summary>
public sealed class ProgressCadenceTests
{
    private sealed class CountingReporter : IProgressReporter
    {
        public ConcurrentQueue<long> Offsets { get; } = new();

        public void Report(string message, long? current = null, long? max = null)
        {
            if (current is long at)
                Offsets.Enqueue(at);
        }
    }

    /// <summary>Valid JSON, and a line per element so the line and row scanners have work too.</summary>
    private static byte[] Document(int atLeastBytes)
    {
        var text = new StringBuilder("[\n", atLeastBytes + 16);
        while (text.Length < atLeastBytes)
            text.Append("1,\n");
        text.Append("1]\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    public static TheoryData<string> Scanners => new() { "json", "raw", "lines" };

    [Theory]
    [MemberData(nameof(Scanners))]
    public async Task ASmallFile_ReportsOnlyItsEnd(string scanner)
    {
        var bytes = Document(64 * 1024);
        var source = new MemoryByteSource(bytes);
        var reporter = new CountingReporter();

        Task indexing = scanner switch
        {
            "json" => JsonStructureIndex.StartIndexing(source, reporter).IndexingTask,
            "raw" => RawSegmentIndex.StartIndexing(source, 160, reporter).IndexingTask,
            _ => FileOffsetIndex.StartIndexing(source, reporter).IndexingTask,
        };
        await indexing;

        Assert.Equal(bytes.Length, reporter.Offsets.Last());
        Assert.InRange(reporter.Offsets.Count, 1, 2);
    }
}
