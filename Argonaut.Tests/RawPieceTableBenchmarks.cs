using System.Text;
using Argonaut.Infrastructure;
using Argonaut.Features.Raw;
using BenchmarkDotNet.Attributes;

namespace Argonaut.Tests;

/// <summary>
/// The cost every <i>read</i> through the piece table pays: resolving a logical offset to a
/// physical one is a binary search over the piece list, so it grows with the number of edits and
/// not with the size of the file. The parameter sweeps piece counts up to roughly where the row
/// index gives up and rebuilds, so the worst case a user can actually reach is on the chart.
///
/// Typing is measured separately in <see cref="RawEditKeystrokeBenchmarks"/>, which needs a very
/// different job configuration - it mutates the document it measures.
/// </summary>
[MemoryDiagnoser]
public class RawPieceTableBenchmarks
{
    /// <summary>Enough lines to span many anchor buckets without making setup dominate.</summary>
    private const int LineCount = 20_000;

    /// <summary>Offsets probed per invocation, spread across the whole document.</summary>
    private const int ProbeCount = 1024;

    /// <summary>Pieces to fragment the document into before measuring a read.</summary>
    [Params(1, 64, 1024)]
    public int PieceCount { get; set; }

    private RawPieceTable fragmented = null!;
    private long[] probeOffsets = null!;

    [GlobalSetup]
    public void Setup()
    {
        var text = new StringBuilder();
        for (int i = 0; i < LineCount; i++)
            text.Append($"line {i}: the quick brown fox jumps over the lazy dog\n");

        // A document fragmented by inserting single bytes at intervals, which is the shape a long
        // editing session produces.
        this.fragmented = new RawPieceTable(new MemoryByteSource(Encoding.UTF8.GetBytes(text.ToString())));
        long stride = Math.Max(1, this.fragmented.AvailableLength / Math.Max(PieceCount, 1));
        for (int i = 1; i < PieceCount; i++)
            this.fragmented.Insert(Math.Min(i * stride, this.fragmented.AvailableLength), "x"u8);

        // Spread the probes, so the binary search is exercised rather than one hot piece being
        // measured over and over.
        this.probeOffsets = new long[ProbeCount];
        for (int i = 0; i < ProbeCount; i++)
            this.probeOffsets[i] = this.fragmented.AvailableLength * i / ProbeCount;
    }

    [Benchmark(OperationsPerInvoke = ProbeCount)]
    public int ResolveOffsets()
    {
        int total = 0;
        foreach (long offset in this.probeOffsets)
            total += this.fragmented.GetContiguousSpan(offset, 1).Length;

        return total;
    }
}
