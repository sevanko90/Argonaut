using System.Text.Json;

namespace Argonaut.Tests.Benchmarks;

public class JsonShapeCorpusTests
{
    [Theory]
    [InlineData(JsonShape.TokenDenseArray)]
    [InlineData(JsonShape.DeepNesting)]
    [InlineData(JsonShape.RecordArray)]
    public void Write_ProducesValidJsonOfExactlyTheRequestedSize(JsonShape shape)
    {
        const long size = 256 * 1024;
        string path = Path.Combine(Path.GetTempPath(), $"corpus-{shape}-{Guid.NewGuid():N}.json");
        try
        {
            JsonShapeCorpus.Write(path, shape, size);

            Assert.Equal(size, new FileInfo(path).Length);
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 64 });
            Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
            Assert.True(document.RootElement.GetArrayLength() > 10);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
