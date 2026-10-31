using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Xml.Indexing;
using Argonaut.Features.Xml.Tree;
using Argonaut.Ui.Hints;
using Argonaut.Ui.Tree;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace Argonaut.Tests.Benchmarks;

/// <summary>
/// What it costs to draw XML rows, per <see cref="XmlShape"/>: a screen scrolled through a fully
/// expanded tree, and a long scroll of twenty screens in one operation. Collapsed summary rows,
/// whose child counts are a different cost, are <see cref="XmlTreeCollapsedPaintBenchmarks"/>'.
///
/// These are per-operation costs. A footprint that climbs with distance travelled does not show in
/// a benchmark's mean; that is <see cref="XmlTreeFootprintTests"/>' job.
///
/// Allocation here is the row text and its hints, so a screen's bytes are what a scroll throws
/// away.
///
/// <b>Size.</b> 16 MiB, not the file sizes the view is built for: a row is decoded from a bounded
/// span and the cursor's walks are bounded by the checkpoint stride and the tree's depth, so the
/// per-row cost is the same at 16 MiB as at 64 - measured at both, they agree within noise. A
/// bigger corpus would write and index more before every case and change nothing being measured.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class XmlTreePaintBenchmarks
{
    private const int ScreenRows = 50;

    /// <summary>Twenty screens in one operation - enough for the per-row cost to be read off, and
    /// not so much that an iteration is spent walking the document.</summary>
    private const int LongScrollRows = 1_000;

    /// <summary>Deep enough to expand every container of every shape in the corpus.</summary>
    private const int FullyExpanded = XmlShapeCorpus.NestingDepth + 4;

    [Params(XmlShape.TinyElements, XmlShape.DeepNesting, XmlShape.RecordRoot, XmlShape.TextHeavy)]
    public XmlShape Shape { get; set; }

    [Params(16)]
    public int SizeMiB { get; set; }

    private string path = null!;
    private MMapFile file = null!;
    private XmlSparseIndex index = null!;
    private XmlTreePainter painter = null!;
    private TreeCursor screenCursor = null!;
    private TreeCursor longCursor = null!;
    private readonly List<TreeRun> runs = new(128);

    [GlobalSetup]
    public void Setup()
    {
        path = Path.Combine(Path.GetTempPath(), $"bench-{Shape}-{Guid.NewGuid():N}.xml");
        XmlShapeCorpus.Write(path, Shape, SizeMiB * 1024L * 1024L);
        file = new MMapFile(path);
        index = XmlSparseIndex.StartIndexing(file);
        index.IndexingTask.GetAwaiter().GetResult();

        var reader = new XmlTreeReader(file);
        painter = new XmlTreePainter(new XmlTreeText(file, index.Structure, reader),
            ValueHintProviders.Standard(new DateHintSettings(), TimeProvider.System));

        var expanded = new TreeExpandState(FullyExpanded);
        screenCursor = new TreeCursor(index.Structure, reader, expanded);
        longCursor = new TreeCursor(index.Structure, reader, expanded);
        screenCursor.SeekTo(file.AvailableLength / 2);
        longCursor.SeekTo(file.AvailableLength / 2);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        file.Dispose();
        File.Delete(path);
    }

    /// <summary>Scroll a screen through a fully expanded tree, decoding each row as it arrives.
    /// Wraps at the end so every call draws a fresh screen.</summary>
    [Benchmark]
    public int Painter_ScrollScreen() => Paint(screenCursor, ScreenRows);

    /// <summary>Twenty screens in one operation: the shape a long scroll has. Its time and
    /// allocation should be twenty screens' worth; more per row than a single screen means a
    /// scroll costs more the further it goes.</summary>
    [Benchmark]
    public int Painter_LongScroll() => Paint(longCursor, LongScrollRows);

    private int Paint(TreeCursor cursor, int rows)
    {
        int painted = 0;
        for (int row = 0; row < rows; row++)
        {
            if (!cursor.MoveNext() && !cursor.MoveToStart())
                break;

            runs.Clear();
            painter.AppendRuns(cursor.Current, runs);
            painted += runs.Count;
        }

        return painted;
    }
}
