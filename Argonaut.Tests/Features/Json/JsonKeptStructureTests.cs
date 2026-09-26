using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Json.Schema;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Features.Json;

/// <summary>
/// A switch to the text view and back closes the JSON view and opens a new one over the same
/// origin. A finished scan's structure is kept on the origin, so the new view opens complete with
/// no scan - unless the bytes changed since, which a save from the text view does.
/// </summary>
public sealed class JsonKeptStructureTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"kept-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(path);

    private static string Document(int items)
    {
        var json = new StringBuilder("{\"features\":[");
        for (int i = 0; i < items; i++)
            json.Append(i == 0 ? "" : ",").Append($"{{\"id\":{i},\"coordinates\":[[{i},1],[{i},2]]}}");
        return json.Append("]}").ToString();
    }

    private static JsonViewModel NewViewModel() => new(new JsonViewSettings(), new SchemaBindings(), TestSchemas.Catalog());

    /// <summary>The structure kept on <paramref name="origin"/>, once the completion monitor has
    /// run - it resumes after the scan's task, so a test that only awaited that task can get here
    /// first.</summary>
    private static async Task<JsonKeptStructure?> KeptStructureAsync(IByteOrigin origin, bool expectOne = true)
    {
        for (int attempt = 0; attempt < (expectOne ? 200 : 20); attempt++)
        {
            if (ByteOriginVersion.Of(origin) is { } version &&
                origin.KeptIndexes.TryGet<JsonKeptStructure>(JsonKeptStructure.Key, version, out var kept))
            {
                return kept;
            }

            await Task.Delay(10);
        }

        return expectOne ? throw new TimeoutException("The finished scan's structure was never kept.") : null;
    }

    [Fact]
    public async Task LoadAsync_OverAnOriginAlreadyIndexed_ReusesItsStructure()
    {
        File.WriteAllText(path, Document(5000));
        var origin = new FileByteOrigin(path);
        var first = NewViewModel();
        await first.LoadAsync(origin);
        await first.IndexingTask;
        var kept = await KeptStructureAsync(origin);
        first.Dispose();

        var second = NewViewModel();
        try
        {
            await second.LoadAsync(origin);

            Assert.True(second.IndexingTask.IsCompletedSuccessfully);
            Assert.True(second.Index!.AllItemsPublished);
            Assert.Same(kept!.Structure, second.Index.Structure);
        }
        finally
        {
            second.Dispose();
            origin.Dispose();
        }
    }

    /// <summary>A save from the text view - or another program's edit - rewrites the file, and a
    /// structure for the old bytes would put rows in the wrong places.</summary>
    [Fact]
    public async Task LoadAsync_AfterTheFileChanged_IndexesAgain()
    {
        File.WriteAllText(path, Document(5000));
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var origin = new FileByteOrigin(path);
        var first = NewViewModel();
        await first.LoadAsync(origin);
        await first.IndexingTask;
        await KeptStructureAsync(origin);
        var firstStructure = first.Index!.Structure;
        first.Dispose();

        File.WriteAllText(path, Document(10));

        var second = NewViewModel();
        try
        {
            await second.LoadAsync(origin);
            await second.IndexingTask;

            Assert.NotSame(firstStructure, second.Index!.Structure);
            Assert.Equal(new FileInfo(path).Length, second.Index.Structure.ScannedTo);
        }
        finally
        {
            second.Dispose();
            origin.Dispose();
        }
    }

    /// <summary>A reopened index would not report the failure, so an invalid document is scanned
    /// again rather than kept.</summary>
    [Fact]
    public async Task AnInvalidDocument_IsNotKept()
    {
        File.WriteAllText(path, Document(100)[..^1]);
        var origin = new FileByteOrigin(path);
        var vm = NewViewModel();
        try
        {
            await vm.LoadAsync(origin);
            await Assert.ThrowsAnyAsync<Exception>(() => vm.IndexingTask);

            Assert.Null(await KeptStructureAsync(origin, expectOne: false));
        }
        finally
        {
            vm.Dispose();
            origin.Dispose();
        }
    }

    /// <summary>An NDJSON line's document indexes only its line; kept, it would reopen as the
    /// structure of the whole file.</summary>
    [Fact]
    public async Task ALineOfALargerFile_IsNotKept()
    {
        string line = Document(10);
        File.WriteAllText(path, line + "\n" + line + "\n");
        var origin = new FileByteOrigin(path);
        var vm = NewViewModel();
        try
        {
            await vm.LoadAsync(origin, line.Length + 1, line.Length);
            await vm.IndexingTask;

            Assert.Null(await KeptStructureAsync(origin, expectOne: false));
        }
        finally
        {
            vm.Dispose();
            origin.Dispose();
        }
    }

    [Fact]
    public void Reopen_OverBytesOfAnotherLength_Throws()
    {
        byte[] json = Encoding.UTF8.GetBytes(Document(100));
        var index = JsonSparseIndex.StartIndexing(new MemoryByteSource(json));
        index.IndexingTask.GetAwaiter().GetResult();
        var kept = index.DetachStructure()!;

        Assert.Throws<ArgumentException>(() => JsonSparseIndex.Reopen(new MemoryByteSource(json[..^1]), kept));
    }
}
