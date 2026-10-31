using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Xml.Indexing;
using Argonaut.Features.Xml.Tree;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace Argonaut.Tests.Benchmarks;

/// <summary>
/// What it costs to move around a fully indexed document in the XML tree, per
/// <see cref="XmlShape"/>: a seek to a sampled offset the way a search hit lands (seek there, show
/// a screen from it), and paging a screen at a time backward and forward through a fully expanded
/// tree. The rows are positions, not decoded text - decoding a screen is
/// <see cref="XmlTreePaintBenchmarks"/>'.
///
/// <b>Sampling.</b> A seek reads forward from the nearest checkpoint, so its cost is anything from
/// nothing to one 64 KB stride depending on where the target falls between checkpoints. One
/// position - the middle of the file, say - measures that alignment, not the seek; it moved 2x
/// between two runs of the same code. The seek rotates through <see cref="SeekSamples"/> positions
/// spread across the document, one per invocation, so the reported mean is a seek anywhere.
///
/// <b>Size.</b> 16 MiB. The read is bounded by the checkpoint stride and the tree's depth, not by
/// the document's length, and the sampled positions cover the stride. The bigger corpus would only
/// be written and indexed before every case.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class XmlTreeNavigationBenchmarks
{
    private const int ScreenRows = 50;

    /// <summary>Positions the seek rotates through, so the mean covers the whole stride.</summary>
    private const int SeekSamples = 8;

    /// <summary>Deep enough to expand every container of every shape in the corpus.</summary>
    private const int FullyExpanded = XmlShapeCorpus.NestingDepth + 4;

    [Params(XmlShape.TinyElements, XmlShape.DeepNesting, XmlShape.RecordRoot, XmlShape.TextHeavy)]
    public XmlShape Shape { get; set; }

    [Params(16)]
    public int SizeMiB { get; set; }

    private string path = null!;
    private MMapFile file = null!;
    private XmlSparseIndex index = null!;
    private TreeCursor seekCursor = null!;
    private TreeCursor pageCursor = null!;
    private TreeCursor forwardCursor = null!;
    private long[] seekOffsets = null!;
    private int nextSeek;

    [GlobalSetup]
    public void Setup()
    {
        path = Path.Combine(Path.GetTempPath(), $"bench-{Shape}-{Guid.NewGuid():N}.xml");
        XmlShapeCorpus.Write(path, Shape, SizeMiB * 1024L * 1024L);
        file = new MMapFile(path);
        index = XmlSparseIndex.StartIndexing(file);
        index.IndexingTask.GetAwaiter().GetResult();

        var expand = new TreeExpandState(FullyExpanded);
        seekCursor = new TreeCursor(index.Structure, new XmlTreeReader(file), expand);
        pageCursor = new TreeCursor(index.Structure, new XmlTreeReader(file), expand);
        forwardCursor = new TreeCursor(index.Structure, new XmlTreeReader(file), expand);
        pageCursor.SeekTo(file.AvailableLength / 2);
        forwardCursor.SeekTo(file.AvailableLength / 2);

        seekOffsets = new long[SeekSamples];
        for (int sample = 0; sample < SeekSamples; sample++)
            seekOffsets[sample] = file.AvailableLength * (2 * sample + 1) / (2 * SeekSamples);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        file.Dispose();
        File.Delete(path);
    }

    /// <summary>Seek to the next sampled position and step through a screen of rows.</summary>
    [Benchmark]
    public long SparseIndex_SeekToSample()
    {
        long offset = seekOffsets[nextSeek];
        nextSeek = (nextSeek + 1) % SeekSamples;

        seekCursor.SeekTo(offset);
        long reached = seekCursor.Current.Start;
        for (int row = 1; row < ScreenRows && seekCursor.MoveNext(); row++)
        {
        }

        return reached;
    }

    /// <summary>One screen further back each call through the fully expanded tree, wrapping at
    /// the top. Nothing is capped: the middle is the middle of the file.</summary>
    [Benchmark]
    public long SparseIndex_PageBackward()
    {
        for (int row = 0; row < ScreenRows; row++)
        {
            if (!pageCursor.MovePrevious())
                pageCursor.MoveToEnd();
        }

        return pageCursor.Current.Start;
    }

    /// <summary>One screen on each call, the direction a reader scrolls.</summary>
    [Benchmark]
    public long SparseIndex_PageForward()
    {
        for (int row = 0; row < ScreenRows; row++)
        {
            if (!forwardCursor.MoveNext())
                forwardCursor.MoveToStart();
        }

        return forwardCursor.Current.Start;
    }
}
