using System.Text;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Schema;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Covers reading the outermost object's property names off the token index - the evidence
/// <see cref="JsonSchemaRootMatcher"/> scores schema types against.
/// </summary>
public class JsonDocumentKeySamplerTests
{
    /// <summary>Indexes a temp file fully. The mapping is disposed before the file is deleted,
    /// since indexing keeps it mapped (see JsonStructureIndexTests for the same contract).</summary>
    private static async Task WithIndexedAsync(string json, Action<JsonStructureIndex, MMapFile> body)
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, json);

        var file = new MMapFile(path);
        try
        {
            var index = JsonStructureIndex.StartIndexing(file);
            await index.IndexingTask;
            body(index, file);
        }
        finally
        {
            file.Dispose();
            try { File.Delete(path); } catch { /* best-effort test cleanup */ }
        }
    }

    private static string[] Decode(IReadOnlyList<byte[]> keys)
        => keys.Select(k => Encoding.UTF8.GetString(k)).ToArray();

    [Fact]
    public Task ObjectRoot_ReadsItsDirectMemberNames()
        => WithIndexedAsync("""
            { "reference": "ABC", "passengers": [1, 2], "flight": { "number": "U28532" } }
            """,
            (index, file) =>
            {
                var keys = JsonDocumentKeySampler.ReadRootKeys(index, file, out bool fromArray);

                // Direct members only - `number` is a grandchild and must not appear.
                Assert.Equal(new[] { "reference", "passengers", "flight" }, Decode(keys));
                Assert.False(fromArray);
            });

    [Fact]
    public Task ArrayRoot_SamplesTheFirstElement()
        => WithIndexedAsync("""
            [ { "line1": "1 High St", "city": "Luton" }, { "line1": "2 Low St", "city": "Hove" } ]
            """,
            (index, file) =>
            {
                var keys = JsonDocumentKeySampler.ReadRootKeys(index, file, out bool fromArray);

                Assert.Equal(new[] { "line1", "city" }, Decode(keys));
                Assert.True(fromArray);
            });

    [Fact]
    public Task ArrayOfScalars_YieldsNothing()
        => WithIndexedAsync("[1, 2, 3]", (index, file) =>
        {
            Assert.Empty(JsonDocumentKeySampler.ReadRootKeys(index, file, out _));
        });

    [Fact]
    public Task ScalarRoot_YieldsNothing()
        => WithIndexedAsync("42", (index, file) =>
        {
            Assert.Empty(JsonDocumentKeySampler.ReadRootKeys(index, file, out _));
        });

    [Fact]
    public Task EmptyObject_YieldsNothing()
        => WithIndexedAsync("{}", (index, file) =>
        {
            Assert.Empty(JsonDocumentKeySampler.ReadRootKeys(index, file, out _));
        });

    [Fact]
    public Task KeyCount_IsCapped()
    {
        var sb = new StringBuilder("{");
        for (int i = 0; i < JsonDocumentKeySampler.MaxKeys * 3; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append($"\"k{i}\":{i}");
        }

        sb.Append('}');

        return WithIndexedAsync(sb.ToString(), (index, file) =>
        {
            // The cost of looking has to stay bounded, and past the cap nothing further
            // discriminates between candidate types.
            Assert.Equal(JsonDocumentKeySampler.MaxKeys, JsonDocumentKeySampler.ReadRootKeys(index, file, out _).Count);
        });
    }

    [Fact]
    public Task ReadMemberNames_ReadsAnyNamedContainer()
        => WithIndexedAsync("""
            { "data": { "line1": "1 High St", "city": "Luton" }, "meta": {} }
            """,
            (index, file) =>
            {
                // The wrapper root offers nothing to match on; the payload one level down does.
                // This is the entry point the per-node match affordance will use.
                Assert.Equal(new[] { "data", "meta" }, Decode(JsonDocumentKeySampler.ReadRootKeys(index, file, out _)));
                Assert.Equal(new[] { "line1", "city" }, Decode(JsonDocumentKeySampler.ReadMemberNames(index, file, 1)));
            });

    [Fact]
    public Task ReadMemberNames_OnANonObject_YieldsNothing()
        => WithIndexedAsync("""{ "items": [1, 2] }""", (index, file) =>
        {
            Assert.Empty(JsonDocumentKeySampler.ReadMemberNames(index, file, 1));
            Assert.Empty(JsonDocumentKeySampler.ReadMemberNames(index, file, 9999));
        });
}
