using Argonaut.Engine.Bytes;
using Argonaut.Features.Xml.Indexing;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Benchmarks;

/// <summary>
/// The benchmark corpora must be well-formed XML of exactly the requested size, and must have the
/// shape each one claims - otherwise a benchmark measures a document nobody opens, or a bug in the
/// writer hides as a slow navigation result.
/// </summary>
public class XmlShapeCorpusTests
{
    private const long Size = 1024 * 1024;

    [Theory]
    [InlineData(XmlShape.TinyElements)]
    [InlineData(XmlShape.DeepNesting)]
    [InlineData(XmlShape.RecordRoot)]
    [InlineData(XmlShape.TextHeavy)]
    public void Write_ProducesWellFormedXmlOfExactlyTheRequestedSize(XmlShape shape)
    {
        string path = PathOf(shape);
        try
        {
            XmlShapeCorpus.Write(path, shape, Size);

            Assert.Equal(Size, new FileInfo(path).Length);

            // XmlReader accepting the document is the well-formedness check: it throws otherwise.
            var counts = XmlModel.CountWithXmlReader(File.ReadAllBytes(path));
            Assert.True(counts.Elements > 0, $"{shape} holds no elements.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(XmlShape.TinyElements, 1)]
    [InlineData(XmlShape.DeepNesting, XmlShapeCorpus.NestingDepth + 1)]
    [InlineData(XmlShape.RecordRoot, 9)]
    [InlineData(XmlShape.TextHeavy, 2)]
    public void Write_ProducesTheShapeItNames(XmlShape shape, int expectedDepth)
    {
        string path = PathOf(shape);
        try
        {
            XmlShapeCorpus.Write(path, shape, Size);
            var nodes = XmlModel.Parse(File.ReadAllBytes(path));

            Assert.Equal(expectedDepth, nodes.Max(n => n.Depth));
            Assert.True(nodes.Count(n => n.Depth == 1) > 10, $"{shape} holds no repeated children.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TinyElements_AreOneTextOnlyElementEach_SoNumberingHasManySiblings()
    {
        string path = PathOf(XmlShape.TinyElements);
        try
        {
            XmlShapeCorpus.Write(path, XmlShape.TinyElements, Size);
            var children = XmlModel.Parse(File.ReadAllBytes(path)).Where(n => n.Depth == 1).ToList();

            Assert.True(children.Count > 50_000, $"Only {children.Count} tiny elements.");
            Assert.All(children, child => Assert.Equal(XmlNodeKind.TextElement, child.Kind));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RecordRoot_IsOneContainerOfCityObjectMembers()
    {
        string path = PathOf(XmlShape.RecordRoot);
        try
        {
            XmlShapeCorpus.Write(path, XmlShape.RecordRoot, Size);
            var nodes = XmlModel.Parse(File.ReadAllBytes(path));

            Assert.Contains(nodes, node => node.Depth == 1 && node.Key == "core:cityObjectMember");
            Assert.True(nodes.Count(node => node.Depth == 1) > 500);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TextHeavy_CarriesALongTextNodeInEveryRecord()
    {
        string path = PathOf(XmlShape.TextHeavy);
        try
        {
            XmlShapeCorpus.Write(path, XmlShape.TextHeavy, Size);
            var nodes = XmlModel.Parse(File.ReadAllBytes(path));
            var posLists = nodes.Where(node => node.Depth == 2 && node.Key == "posList").ToList();

            Assert.True(posLists.Count > 10, $"Only {posLists.Count} coordinate lists.");
            Assert.All(posLists, list =>
                Assert.True(list.End - list.Start >= XmlShapeCorpus.TextNodeBytes,
                    $"A coordinate list is only {list.End - list.Start} bytes."));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(XmlShape.TinyElements)]
    [InlineData(XmlShape.DeepNesting)]
    [InlineData(XmlShape.RecordRoot)]
    [InlineData(XmlShape.TextHeavy)]
    public async Task EveryCorpusDocument_IsAcceptedByTheIndex(XmlShape shape)
    {
        string path = PathOf(shape);
        try
        {
            XmlShapeCorpus.Write(path, shape, Size);

            using var file = new MMapFile(path);
            var index = XmlSparseIndex.StartIndexing(file);
            await index.IndexingTask;

            Assert.Null(index.Failure);
            Assert.True(index.Structure.ContainerCount > 0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The navigation and path benchmarks report a mean over sampled positions on the
    /// grounds that the samples cover the checkpoint stride. Evenly spaced offsets alone did not:
    /// every TinyElements sample landed a whole stride past its checkpoint.</summary>
    [Theory]
    [InlineData(XmlShape.TinyElements)]
    [InlineData(XmlShape.DeepNesting)]
    [InlineData(XmlShape.RecordRoot)]
    [InlineData(XmlShape.TextHeavy)]
    public async Task SampleOffsets_LandAcrossTheCheckpointStride(XmlShape shape)
    {
        string path = PathOf(shape);
        try
        {
            XmlShapeCorpus.Write(path, shape, 16 * Size);

            using var file = new MMapFile(path);
            var index = XmlSparseIndex.StartIndexing(file);
            await index.IndexingTask;
            var structure = index.Structure;
            var checkpoints = Enumerable.Range(0, structure.CheckpointCount)
                .Select(checkpoint => structure.GetCheckpoint(checkpoint).Offset).ToArray();

            var pastCheckpoint = XmlShapeCorpus.SampleOffsets(file.AvailableLength, 8, structure.CheckpointBytes)
                .Select(offset => offset - checkpoints.Where(start => start <= offset).DefaultIfEmpty(0).Max())
                .ToArray();

            Assert.True(pastCheckpoint.Max() - pastCheckpoint.Min() > structure.CheckpointBytes / 2,
                $"Samples sit {string.Join(", ", pastCheckpoint)} bytes past their checkpoints.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string PathOf(XmlShape shape) => Path.Combine(Path.GetTempPath(), $"corpus-{shape}-{Guid.NewGuid():N}.xml");
}
