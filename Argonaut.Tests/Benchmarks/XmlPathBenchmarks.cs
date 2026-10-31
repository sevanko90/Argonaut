using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Xml.Indexing;
using Argonaut.Features.Xml.Tree;
using Argonaut.Ui.Tree;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace Argonaut.Tests.Benchmarks;

/// <summary>
/// What a selection costs in the XML tree, per <see cref="XmlShape"/>: spelling the XPath of a row
/// (a step per ancestor, each numbered by how many same-name siblings stand before it), formatting
/// it, and resolving that path back to a row. The three shapes put the numbering on each of its
/// paths - a container with hundreds of thousands of same-name siblings, a deep chain of containers
/// the index never records, and a namespaced record root - so a regression in sibling counting
/// shows up wherever it happens.
///
/// <b>Sampling.</b> Numbering reads from the nearest checkpoint, so a step costs between nothing
/// and one 64 KB stride of siblings depending on where the row falls. One row - the last in the
/// document, say - measures that alignment rather than the selection, and it moved by 2.6x between
/// two runs of the same code. Rows are taken from <see cref="SampleCount"/> positions spread across
/// the document, rotated one per invocation, so the reported mean is a selection anywhere.
///
/// <b>Size.</b> 16 MiB. A step is bounded by the checkpoint stride, and the document's length only
/// decides how large <c>k</c> is, which is a stored count rather than a walk.
///
/// Allocation is the segment list and the strings it holds: one selection's worth, thrown away
/// when the selection moves.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class XmlPathBenchmarks
{
    /// <summary>Rows the benchmarks rotate through, so the mean covers the whole stride.</summary>
    private const int SampleCount = 8;

    /// <summary>Deep enough to expand every container of every shape in the corpus.</summary>
    private const int FullyExpanded = XmlShapeCorpus.NestingDepth + 4;

    [Params(XmlShape.TinyElements, XmlShape.DeepNesting, XmlShape.RecordRoot)]
    public XmlShape Shape { get; set; }

    [Params(16)]
    public int SizeMiB { get; set; }

    private string path = null!;
    private MMapFile file = null!;
    private XmlSparseIndex index = null!;
    private XmlTreeReader reader = null!;
    private XmlTreeText text = null!;
    private TreeCursor[] sampleCursors = null!;
    private string[] samplePaths = null!;
    private int nextSample;

    [GlobalSetup]
    public void Setup()
    {
        path = Path.Combine(Path.GetTempPath(), $"bench-{Shape}-{Guid.NewGuid():N}.xml");
        XmlShapeCorpus.Write(path, Shape, SizeMiB * 1024L * 1024L);
        file = new MMapFile(path);
        index = XmlSparseIndex.StartIndexing(file);
        index.IndexingTask.GetAwaiter().GetResult();

        reader = new XmlTreeReader(file);
        text = new XmlTreeText(file, index.Structure, reader);

        sampleCursors = new TreeCursor[SampleCount];
        samplePaths = new string[SampleCount];
        for (int sample = 0; sample < SampleCount; sample++)
        {
            var cursor = new TreeCursor(index.Structure, reader, new TreeExpandState(FullyExpanded));
            cursor.SeekTo(file.AvailableLength * (2 * sample + 1) / (2 * SampleCount));

            sampleCursors[sample] = cursor;
            samplePaths[sample] = XmlTreePaths.Format(XmlTreePaths.Segments(cursor, text, index));

            // The paths below are measured, not asserted; this keeps a corpus or parser change
            // from quietly benchmarking a walk that returns nothing.
            if (XmlTreePaths.Resolve(index, reader, text, samplePaths[sample]).Target != cursor.Current.Node.RowStart)
                throw new InvalidOperationException($"'{samplePaths[sample]}' did not resolve back to its row.");
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        file.Dispose();
        File.Delete(path);
    }

    /// <summary>The segments of the selected row's XPath, a step per ancestor.</summary>
    [Benchmark]
    public IReadOnlyList<TreePathSegment> Segments_SampledRow()
        => XmlTreePaths.Segments(sampleCursors[NextSample()], text, index);

    /// <summary>What the path bar shows: the same segments joined into one string.</summary>
    [Benchmark]
    public string SegmentsAndFormat_SampledRow()
        => XmlTreePaths.Format(XmlTreePaths.Segments(sampleCursors[NextSample()], text, index));

    /// <summary>Typing that path into the path bar: parse it, then walk the document to the row
    /// it names, counting siblings from the nearest checkpoint.</summary>
    [Benchmark]
    public long Resolve_SampledRowPath()
        => XmlTreePaths.Resolve(index, reader, text, samplePaths[NextSample()]).Target ?? -1;

    /// <summary>The next sampled row, rotating so the mean is taken over every position.</summary>
    private int NextSample()
    {
        int sample = nextSample;
        nextSample = (nextSample + 1) % SampleCount;
        return sample;
    }
}
