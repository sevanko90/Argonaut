using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Xml;
using Argonaut.Features.Xml.Indexing;
using Argonaut.Features.Xml.Tree;
using Argonaut.Ui.Hints;
using Argonaut.Ui.Tree;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace Argonaut.Tests.Benchmarks;

/// <summary>
/// What a screen of collapsed summary rows costs: every one asks its container how many children
/// it has, which for a container the index did not record is counted by reading it. That is two to
/// three times an expanded row, and it is the state a document opens in, so it is measured on its
/// own rather than as a flag on <see cref="XmlTreePaintBenchmarks"/>.
///
/// Only the shapes that have containers below the default expand depth are here: with
/// <see cref="XmlShape.TinyElements"/> the root is the only container and is expanded, and with
/// <see cref="XmlShape.TextHeavy"/> the children of a record are leaves - collapsing either draws
/// exactly the expanded screen, so measuring it would report the same number twice.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class XmlTreeCollapsedPaintBenchmarks
{
    private const int ScreenRows = 50;

    [Params(XmlShape.DeepNesting, XmlShape.RecordRoot)]
    public XmlShape Shape { get; set; }

    [Params(16)]
    public int SizeMiB { get; set; }

    private string path = null!;
    private MMapFile file = null!;
    private XmlSparseIndex index = null!;
    private XmlTreePainter painter = null!;
    private TreeCursor cursor = null!;
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
        cursor = new TreeCursor(index.Structure, reader, new TreeExpandState(XmlViewSettings.DefaultExpandDepth));
        cursor.SeekTo(file.AvailableLength / 2);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        file.Dispose();
        File.Delete(path);
    }

    /// <summary>A screen as a document first opens: containers below the remembered depth are
    /// summaries, so each row on the way down counts its children.</summary>
    [Benchmark]
    public int Painter_CollapsedScreen()
    {
        int painted = 0;
        for (int row = 0; row < ScreenRows; row++)
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
