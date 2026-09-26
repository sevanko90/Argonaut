using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Json.Paths;
using Argonaut.Features.Json.Tree;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Features.Json.Tree;

/// <summary>
/// Paths in the sparse tree against the dense tree's: every row's path is the one
/// <see cref="JsonPathBuilder"/> writes for the same token, and resolving it lands back on that
/// row - through recorded containers and checkpoints, since the sizes here are tiny.
/// </summary>
public sealed class JsonTreePathsTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"paths-{Guid.NewGuid():N}.json");
    private MMapFile? file;

    public void Dispose()
    {
        file?.Dispose();
        File.Delete(path);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EveryRowsPathMatchesTheDenseTreeAndResolvesBackToIt(int seed)
    {
        File.WriteAllBytes(path, RandomJson.LargeContainers(new Random(seed), elements: 40));
        file = new MMapFile(path);

        var dense = JsonStructureIndex.StartIndexing(file);
        dense.IndexingTask.GetAwaiter().GetResult();
        var denseRows = new JsonVisibleRowCollection(dense, file, defaultExpandDepth: 30);

        var sparse = JsonSparseIndex.StartIndexing(file, promotionBytes: 256, checkpointBytes: 64);
        sparse.IndexingTask.GetAwaiter().GetResult();
        var reader = new JsonTreeReader(file);
        var text = new JsonTreeText(file, sparse.Structure, reader);
        var cursor = new TreeCursor(sparse.Structure, reader, new TreeExpandState(30));

        int i = 0, checkedRows = 0;
        for (bool more = cursor.MoveToStart(); more; more = cursor.MoveNext(), i++)
        {
            var row = (JsonRow)denseRows[i]!;
            if (cursor.Current.Shape == TreeRowShape.Close)
                continue;

            string expected = JsonPathBuilder.Build(dense, file, row.TokenIndex);
            var segments = JsonTreePaths.Segments(cursor, text);
            Assert.Equal(expected, JsonTreePaths.Format(segments));
            Assert.Equal(cursor.Current.Start, segments[^1].Target);

            // Where the dense resolver lands - not necessarily this row, since a random object can
            // repeat a name and a path then names the first.
            var denseResolved = JsonPathResolver.ResolveAsync(dense, file, expected).GetAwaiter().GetResult();
            var token = dense.GetToken(denseResolved.TokenIndex!.Value);
            long denseRowStart = token.NameLength >= 0 ? token.NameOffset - 1
                : token.Kind == JsonTokenKind.String ? token.Offset - 1
                : token.Offset;

            var resolved = JsonTreePaths.Resolve(sparse.Structure, reader, text, expected);
            Assert.Null(resolved.Error);
            Assert.Equal(denseRowStart, resolved.Target);
            checkedRows++;
        }

        Assert.True(checkedRows > 200);
    }

    [Theory]
    [InlineData("$.nope")]
    [InlineData("$[0]")]
    [InlineData("$.a[9]")]
    [InlineData("$.a[0].b")]
    [InlineData("$.a[1].x")]
    [InlineData("")]
    [InlineData("$.a[")]
    public async Task ErrorsReadAsTheDenseResolversDo(string query)
    {
        File.WriteAllText(path, """{"a":[{"b":1},{"x":"s"}],"weird key":{}}""");
        file = new MMapFile(path);

        var dense = JsonStructureIndex.StartIndexing(file);
        await dense.IndexingTask;
        var expected = await JsonPathResolver.ResolveAsync(dense, file, query);

        var sparse = JsonSparseIndex.StartIndexing(file, promotionBytes: 4, checkpointBytes: 2);
        await sparse.IndexingTask;
        var reader = new JsonTreeReader(file);
        var actual = JsonTreePaths.Resolve(sparse.Structure, reader, new JsonTreeText(file, sparse.Structure, reader), query);

        Assert.Equal(expected.Error, actual.Error);
        Assert.Equal(expected.TokenIndex is null, actual.Target is null);
    }
}
