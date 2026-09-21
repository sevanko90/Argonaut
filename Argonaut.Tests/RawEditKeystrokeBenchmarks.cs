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

    /// <summary>
    /// A single unbroken line, for the case that decides how the spans are stored: an edit
    /// inside one has to walk to its end before the two byte streams can be shown to have
    /// rejoined, so the keystroke cost here is the walk and nothing else.
    /// </summary>
    private const int LongLineBytes = 8 * 1024 * 1024;

    private MemoryByteSource source = null!;
    private RawSegmentIndex index = null!;
    private RawPieceTable document = null!;
    private RawEditedRowIndex rows = null!;
    private long caret;

    private MemoryByteSource longLineSource = null!;
    private RawSegmentIndex longLineIndex = null!;
    private RawPieceTable longLineDocument = null!;
    private RawEditedRowIndex longLineRows = null!;
    private long longLineCaret;

    [GlobalSetup]
    public void Setup()
    {
        var text = new StringBuilder();
        for (int i = 0; i < LineCount; i++)
            text.Append($"line {i}: the quick brown fox jumps over the lazy dog\n");

        this.source = new MemoryByteSource(Encoding.UTF8.GetBytes(text.ToString()));
        this.index = RawSegmentIndex.StartIndexing(this.source, WrapWidth);
        this.index.IndexingTask.GetAwaiter().GetResult();

        var longLine = new byte[LongLineBytes];
        longLine.AsSpan().Fill((byte)'a');
        this.longLineSource = new MemoryByteSource(longLine);
        this.longLineIndex = RawSegmentIndex.StartIndexing(this.longLineSource, WrapWidth);
        this.longLineIndex.IndexingTask.GetAwaiter().GetResult();
    }

    [IterationSetup]
    public void ResetDocument()
    {
        this.document = new RawPieceTable(this.source);
        this.rows = new RawEditedRowIndex(this.index, this.source, this.document);
        this.caret = this.source.AvailableLength / 2;

        this.longLineDocument = new RawPieceTable(this.longLineSource);
        this.longLineRows = new RawEditedRowIndex(this.longLineIndex, this.longLineSource, this.longLineDocument);
        this.longLineCaret = 1024;
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

    /// <summary>
    /// The same keystroke inside one unbroken line, which is the worst case the design has and
    /// the one that decided spans hold anchors rather than rows. It is reported per character so
    /// it sits beside <see cref="TypeCharacters"/>, but it is not the same kind of number: this
    /// one grows with the length of the line, at roughly 30ns per row walked.
    /// </summary>
    [Benchmark(OperationsPerInvoke = KeystrokeBurst)]
    public int TypeCharactersInsideALongLine()
    {
        for (int i = 0; i < KeystrokeBurst; i++)
        {
            this.longLineRows.ApplyEdit(this.longLineDocument.Insert(this.longLineCaret, "z"u8));
            this.longLineCaret++;
        }

        return this.longLineRows.RowCount;
    }
}
