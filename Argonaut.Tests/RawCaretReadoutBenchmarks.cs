using System.Text;
using Argonaut.Infrastructure;
using Argonaut.Features.Raw;
using Argonaut.Infrastructure.Unicode;
using BenchmarkDotNet.Attributes;

namespace Argonaut.Tests;

/// <summary>
/// The cost every <i>caret move</i> pays for the status gutter. The gutter is redrawn on each
/// move, including a held arrow key or a drag-select, so anything allocating per call would show
/// up as GC pressure during ordinary interaction rather than as a slow operation anyone notices.
///
/// The cases are the shape of the work, not the file: a normal column is a few dozen bytes of
/// scanning, a column at the cap is a megabyte of vectorized scanning, and a selection adds a
/// character count over its own span.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class RawCaretReadoutBenchmarks
{
    private const int WrapWidth = 160;

    private MemoryByteSource shortLines = null!;
    private RawSegmentIndex shortLineRows = null!;

    private MemoryByteSource oneLongAsciiLine = null!;
    private RawSegmentIndex longLineRows = null!;

    private MemoryByteSource oneLongMixedLine = null!;
    private RawSegmentIndex mixedLineRows = null!;

    [GlobalSetup]
    public void Setup()
    {
        var text = new StringBuilder();
        for (int i = 0; i < 20_000; i++)
            text.Append($"line {i}: the quick brown fox jumps over the lazy dog\n");

        this.shortLines = new MemoryByteSource(Encoding.UTF8.GetBytes(text.ToString()));
        this.shortLineRows = Index(this.shortLines);

        this.oneLongAsciiLine = new MemoryByteSource(Encoding.UTF8.GetBytes(new string('x', 4_000_000)));
        this.longLineRows = Index(this.oneLongAsciiLine);

        this.oneLongMixedLine = new MemoryByteSource(
            Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("héllo wörld ", 340_000))));
        this.mixedLineRows = Index(this.oneLongMixedLine);

        // The name table inflates on first use; measuring that once here keeps it out of the
        // per-call numbers, which is where it belongs - it happens once per session.
        UnicodeNames.NameOf('x');
    }

    private static RawSegmentIndex Index(MemoryByteSource source)
    {
        var index = RawSegmentIndex.StartIndexing(source, WrapWidth);
        index.IndexingTask.GetAwaiter().GetResult();
        return index;
    }

    /// <summary>The ordinary case: a caret partway along a short line.</summary>
    [Benchmark(Baseline = true)]
    public RawCaretReadout ShortLine()
        => RawCaretReadout.Describe(this.shortLineRows, this.shortLines, new RawCaret(500_030), default);

    /// <summary>A caret one byte into a line, where the backwards scan stops immediately.</summary>
    [Benchmark]
    public RawCaretReadout LineStart()
        => RawCaretReadout.Describe(this.shortLineRows, this.shortLines, new RawCaret(500_001), default);

    /// <summary>Selection of a few hundred bytes, which adds a character count over it.</summary>
    [Benchmark]
    public RawCaretReadout ShortLineWithSelection()
        => RawCaretReadout.Describe(
            this.shortLineRows, this.shortLines, new RawCaret(500_030), new RawSelection(500_000, 500_400));

    /// <summary>Worst realistic case: a caret a megabyte into one unbroken ASCII line.</summary>
    [Benchmark]
    public RawCaretReadout ColumnAtTheCap()
        => RawCaretReadout.Describe(
            this.longLineRows, this.oneLongAsciiLine, new RawCaret(RawCaretReadout.ColumnScanBytes), default);

    /// <summary>The same, where the ASCII fast path does not apply and every rune is decoded.</summary>
    [Benchmark]
    public RawCaretReadout ColumnAtTheCapNonAscii()
        => RawCaretReadout.Describe(
            this.mixedLineRows, this.oneLongMixedLine, new RawCaret(RawCaretReadout.ColumnScanBytes), default);

    /// <summary>Past the cap: the column is refused, the line number still is not.</summary>
    [Benchmark]
    public RawCaretReadout PastTheColumnCap()
        => RawCaretReadout.Describe(
            this.longLineRows, this.oneLongAsciiLine, new RawCaret(3_000_000), default);

    [Benchmark]
    public string? UnicodeNameLookup() => UnicodeNames.NameOf(0x2028);
}
