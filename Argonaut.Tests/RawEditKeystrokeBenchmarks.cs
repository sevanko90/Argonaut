using System.Text;
using Argonaut.Infrastructure;
using Argonaut.Features.Raw;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace Argonaut.Tests;

/// <summary>
/// The cost every <i>keystroke</i> pays: folding one edit into <see cref="RawEditedRowIndex"/>,
/// which re-derives the dirty span. This is the number the whole editing design exists to keep
/// small - the alternative it replaces is re-deriving the tail of the file, which at multi-GB is
/// seconds per character.
///
/// Typing mutates the document it is measured against, so this needs `RunStrategy.Monitoring`
/// with one invocation per iteration and an <see cref="IterationSetup"/> that rebuilds the
/// document. Without that the document would grow without bound across a run and the later
/// invocations would be measuring something far more fragmented than the early ones. That job
/// configuration is also why this is a separate class from
/// <see cref="RawPieceTableBenchmarks"/>: applied to a sub-microsecond read benchmark it produces
/// error bars wider than the measurement.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, invocationCount: 1, warmupCount: 3, iterationCount: 20)]
public class RawEditKeystrokeBenchmarks
{
    private const int LineCount = 20_000;

    private const int WrapWidth = 160;

    /// <summary>Characters typed per measured invocation, so the per-character figure reported
    /// through OperationsPerInvoke is not swamped by timer resolution.</summary>
    private const int KeystrokeBurst = 64;

    private MemoryByteSource source = null!;
    private RawSegmentIndex index = null!;
    private RawPieceTable document = null!;
    private RawEditedRowIndex rows = null!;
    private long caret;

    [GlobalSetup]
    public void Setup()
    {
        var text = new StringBuilder();
        for (int i = 0; i < LineCount; i++)
            text.Append($"line {i}: the quick brown fox jumps over the lazy dog\n");

        this.source = new MemoryByteSource(Encoding.UTF8.GetBytes(text.ToString()));
        this.index = RawSegmentIndex.StartIndexing(this.source, WrapWidth);
        this.index.IndexingTask.GetAwaiter().GetResult();
    }

    [IterationSetup]
    public void ResetDocument()
    {
        this.document = new RawPieceTable(this.source);
        this.rows = new RawEditedRowIndex(this.index, this.source, this.document);
        this.caret = this.source.AvailableLength / 2;
    }

    [Benchmark(OperationsPerInvoke = KeystrokeBurst)]
    public int TypeCharacters()
    {
        for (int i = 0; i < KeystrokeBurst; i++)
        {
            this.rows.ApplyEdit(this.document.Insert(this.caret, "z"u8));
            this.caret++;
        }

        return this.rows.RowCount;
    }
}
