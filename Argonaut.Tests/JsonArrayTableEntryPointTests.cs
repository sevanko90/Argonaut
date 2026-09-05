using System.Text;
using Argonaut.Features.Json;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// The "view as table" entry point: which rows offer the link, and - the part that fails
/// illegibly when it is wrong - the byte range the JSON document resolves for the array.
///
/// The range assertions round-trip rather than assert offsets: each opens a real
/// JsonArrayTableSession over the raised range and checks it indexes as a whole JSON document
/// with the expected elements. An off-by-one on either bracket surfaces as a JsonReaderException
/// out of that indexer, which is exactly the failure this is guarding against.
///
/// The static ArrayTableService event is safe to use here because this assembly disables test
/// parallelization (see AssemblyInfo.cs); every subscription is still removed in a finally.
/// </summary>
public class JsonArrayTableEntryPointTests
{
    private static string WriteTempJson(string content)
    {
        string path = Path.GetTempFileName();
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        return path;
    }

    /// <summary>Loads <paramref name="json"/>, clicks "view as table" on the row at
    /// <paramref name="tokenIndex"/>, and hands the request that reached the shell to
    /// <paramref name="assert"/> - which runs while the file still exists, since every
    /// assertion here is about bytes that have to be read back off it.</summary>
    private static async Task WithRequestForAsync(string json, int tokenIndex, Func<ArrayTableRequest?, Task> assert)
    {
        string path = WriteTempJson(json);
        var vm = new JsonViewModel();
        ArrayTableRequest? captured = null;
        void Capture(ArrayTableRequest r) => captured = r;

        ArrayTableService.Requested += Capture;
        try
        {
            await vm.LoadAsync(path);
            await vm.IndexingTask;
            await vm.RequestArrayTableAsync(tokenIndex);
            await assert(captured);
        }
        finally
        {
            ArrayTableService.Requested -= Capture;
            vm.Dispose();
            File.Delete(path);
        }
    }

    /// <summary>Opens the raised range as a table session and reports what it found - the
    /// round-trip that proves the range is a whole, well-formed JSON array.</summary>
    private static async Task<int> ElementsInRangeAsync(ArrayTableRequest request)
    {
        using var session = JsonArrayTableSession.Start(request.Path, request.Offset, request.Length);
        await session.IndexingTask;

        Assert.Null(session.Failure);
        return session.Elements.ElementCount;
    }

    private static async Task<List<JsonRow>> RowsOfAsync(string json)
    {
        string path = WriteTempJson(json);
        var vm = new JsonViewModel();
        try
        {
            await vm.LoadAsync(path);
            await vm.IndexingTask;
            vm.SetDefaultExpandDepth(10);

            var rows = new List<JsonRow>();
            for (int i = 0; i < vm.Rows.Count; i++)
                rows.Add((JsonRow)vm.Rows[i]!);
            return rows;
        }
        finally
        {
            vm.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LinkIsOfferedOnNonEmptyArrayRowsOnly()
    {
        var rows = await RowsOfAsync("""{"items":[1,2],"empty":[],"obj":{"a":1},"scalar":5}""");

        Assert.True(Assert.Single(rows, r => r.Name == "items").CanViewAsTable);
        Assert.False(Assert.Single(rows, r => r.Name == "empty").CanViewAsTable);
        Assert.False(Assert.Single(rows, r => r.Name == "obj").CanViewAsTable);
        Assert.False(Assert.Single(rows, r => r.Name == "scalar").CanViewAsTable);
    }

    [Fact]
    public async Task LinkIsOfferedOnAnArrayOfScalars()
    {
        // Not restricted to arrays of objects: a scalar array tables as one column, and
        // reshaping a flat array into N columns is the second mode's whole reason to exist.
        var rows = await RowsOfAsync("""{"coords":[1,2,3,4]}""");

        Assert.True(Assert.Single(rows, r => r.Name == "coords").CanViewAsTable);
    }

    [Fact]
    public Task RootArray_ResolvesARangeThatIndexesAsAWholeArray()
        => WithRequestForAsync("""[{"id":1},{"id":2},{"id":3}]""", tokenIndex: 0, async request =>
        {
            Assert.NotNull(request);
            Assert.Equal(0, request!.Value.Offset);
            Assert.Equal(3, await ElementsInRangeAsync(request.Value));
        });

    [Fact]
    public async Task NestedArray_ResolvesOnlyItsOwnBytes()
    {
        // Token 0 is the root object, 1 is the "before" array, and the array under "items"
        // follows it - the range must cover that one and nothing around it.
        string json = """{"before":[9,9,9,9,9],"items":[{"id":1},{"id":2}]}""";
        var rows = await RowsOfAsync(json);
        int itemsToken = Assert.Single(rows, r => r.Name == "items").TokenIndex;

        await WithRequestForAsync(json, itemsToken, async request =>
        {
            Assert.NotNull(request);
            Assert.Equal(2, await ElementsInRangeAsync(request!.Value));
            Assert.Equal("$.items", request.Value.OriginPath);
        });
    }

    [Fact]
    public async Task RangeIncludesBothBrackets()
    {
        // The closing bracket is the off-by-one that would otherwise surface as a
        // JsonReaderException out of the table's own indexer rather than as anything legible.
        string json = """{"items":[1,2,3]}""";
        var rows = await RowsOfAsync(json);
        int itemsToken = Assert.Single(rows, r => r.Name == "items").TokenIndex;

        await WithRequestForAsync(json, itemsToken, request =>
        {
            Assert.NotNull(request);
            string bytes = File.ReadAllText(request!.Value.Path)
                .Substring((int)request.Value.Offset, (int)request.Value.Length);
            Assert.Equal("[1,2,3]", bytes);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task LargeStillIndexingArray_WaitsForTheArrayToCloseBeforeResolving()
    {
        // EndIndex is -1 until the container closes, so on a file still being scanned the
        // array's length is not yet known. "Enabled once it has an element" is not a
        // sufficient guard - the request must wait, and then be exact.
        var sb = new StringBuilder("[");
        for (int i = 0; i < 200_000; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append("{\"i\":").Append(i).Append('}');
        }
        sb.Append(']');

        string path = WriteTempJson(sb.ToString());
        var vm = new JsonViewModel();
        ArrayTableRequest? captured = null;
        void Capture(ArrayTableRequest r) => captured = r;

        ArrayTableService.Requested += Capture;
        try
        {
            await vm.LoadAsync(path);
            Assert.False(vm.IndexingTask.IsCompleted); // sanity: the scan is genuinely still running

            await vm.RequestArrayTableAsync(0);

            Assert.NotNull(captured);
            Assert.Equal(new FileInfo(path).Length, captured!.Value.Length);
        }
        finally
        {
            ArrayTableService.Requested -= Capture;
            vm.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SubRangeDocument_DoesNotOfferTheTableAtAll()
    {
        // A per-line NDJSON sub-document's token offsets are mapping-relative, so they are not
        // file offsets and the table would map the wrong bytes. The link is hidden rather than
        // the conversion skipped - see JsonViewModel.SupportsArrayTable.
        string json = "{\"a\":1}\n{\"items\":[1,2,3]}\n";
        string path = WriteTempJson(json);
        int lineOffset = json.IndexOf("{\"items\"", StringComparison.Ordinal);
        int lineLength = json.IndexOf('\n', lineOffset) - lineOffset;

        var vm = new JsonViewModel();
        ArrayTableRequest? captured = null;
        void Capture(ArrayTableRequest r) => captured = r;

        ArrayTableService.Requested += Capture;
        try
        {
            await vm.LoadAsync(path, lineOffset, lineLength);
            await vm.IndexingTask;

            Assert.False(vm.SupportsArrayTable);

            await vm.RequestArrayTableAsync(1); // the "items" array within the line
            Assert.Null(captured);
        }
        finally
        {
            ArrayTableService.Requested -= Capture;
            vm.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task WholeFileDocument_SupportsTheTable()
    {
        string path = WriteTempJson("[1,2,3]");
        var vm = new JsonViewModel();
        try
        {
            await vm.LoadAsync(path);
            Assert.True(vm.SupportsArrayTable);
        }
        finally
        {
            vm.Dispose();
            File.Delete(path);
        }
    }
}
