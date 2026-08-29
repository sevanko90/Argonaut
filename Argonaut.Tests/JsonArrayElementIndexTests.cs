using System.Text;
using Argonaut.Features.Json;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Verifies JsonArrayElementIndex: ordinal-to-token addressing across the sparse anchor stride,
/// every element kind (scalars, objects, nested arrays, empty containers), and the two rules the
/// walk must not get wrong - only ever counting an element whose own container has CLOSED, and
/// publishing once more at completion so an array shorter than one stride is not stuck empty.
///
/// Indexing is always run to completion before asserting, so these never depend on scan timing;
/// the growth path is covered separately by asserting the published count is a stride multiple
/// mid-scan is NOT something a deterministic test can do, so it is expressed instead as "the
/// final count is exact" plus the empty/short-array cases.
/// </summary>
public class JsonArrayElementIndexTests
{
    private sealed class Fixture : IDisposable
    {
        public required MMapFile File { get; init; }
        public required JsonStructureIndex Source { get; init; }
        public required JsonArrayElementIndex Elements { get; init; }
        public required string Path { get; init; }

        public void Dispose()
        {
            File.Dispose();
            System.IO.File.Delete(Path);
        }
    }

    private static async Task<Fixture> BuildAsync(string json, int arrayTokenIndex = 0)
    {
        string path = System.IO.Path.GetTempFileName();
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(json));

        var file = new MMapFile(path);
        var source = JsonStructureIndex.StartIndexing(file);
        var elements = JsonArrayElementIndex.Start(source, arrayTokenIndex);

        // Both faults are the assertions' subject (a malformed document, a non-array root), so
        // they are observed here rather than rethrown - what the scan recorded is what the tests
        // read back off Failure/ElementCount.
        await Task.WhenAll(Observed(source.IndexingTask), Observed(elements.IndexingTask));

        return new Fixture { File = file, Source = source, Elements = elements, Path = path };
    }

    private static async Task Observed(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // Intentionally swallowed - see BuildAsync.
        }
    }

    private static string ScalarArray(int count)
        => "[" + string.Join(',', Enumerable.Range(0, count)) + "]";

    [Fact]
    public async Task EmptyArray_HasNoElements()
    {
        using var f = await BuildAsync("[]");

        Assert.Equal(0, f.Elements.ElementCount);
        Assert.True(f.Elements.IsComplete);
    }

    [Fact]
    public async Task ShortArray_PublishesAtCompletionRatherThanWaitingForAStrideBoundary()
    {
        // Three elements never cross the 64-element stride, so without the completion publish
        // the table would stay empty forever. This is the common case, not an edge case.
        using var f = await BuildAsync(ScalarArray(3));

        Assert.Equal(3, f.Elements.ElementCount);
    }

    [Fact]
    public async Task ScalarElements_AddressEachTokenInOrder()
    {
        using var f = await BuildAsync(ScalarArray(5));

        for (int i = 0; i < 5; i++)
        {
            int token = f.Elements.TokenForElement(i);
            Assert.Equal(JsonTokenKind.Number, f.Source.GetToken(token).Kind);
            Assert.Equal(i + 1, token); // token 0 is the StartArray
        }
    }

    [Fact]
    public async Task ObjectElements_SkipWholeSubtrees()
    {
        using var f = await BuildAsync("""[{"a":1,"b":{"c":2}},{"a":3,"b":{"c":4}},{"a":5,"b":{"c":6}}]""");

        Assert.Equal(3, f.Elements.ElementCount);

        for (int i = 0; i < 3; i++)
        {
            int token = f.Elements.TokenForElement(i);
            var info = f.Source.GetToken(token);
            Assert.Equal(JsonTokenKind.StartObject, info.Kind);
            Assert.Equal(0, info.ParentIndex); // a direct child of the root array
        }

        // Distinct elements, in ascending token order - i.e. subtrees really were skipped.
        Assert.True(f.Elements.TokenForElement(0) < f.Elements.TokenForElement(1));
        Assert.True(f.Elements.TokenForElement(1) < f.Elements.TokenForElement(2));
    }

    [Fact]
    public async Task MixedElementKinds_AreAllAddressable()
    {
        using var f = await BuildAsync("""[1,"two",{"three":3},[4,4],null,true,{},[]]""");

        Assert.Equal(8, f.Elements.ElementCount);

        var kinds = Enumerable.Range(0, 8)
            .Select(i => f.Source.GetToken(f.Elements.TokenForElement(i)).Kind)
            .ToArray();

        Assert.Equal(
            [
                JsonTokenKind.Number, JsonTokenKind.String, JsonTokenKind.StartObject,
                JsonTokenKind.StartArray, JsonTokenKind.Null, JsonTokenKind.True,
                JsonTokenKind.StartObject, JsonTokenKind.StartArray
            ],
            kinds);
    }

    [Fact]
    public async Task ElementsPastTheFirstAnchorBucket_ResolveThroughTheirOwnAnchor()
    {
        // 200 elements spans four stride buckets (64 each), so this exercises the bucket
        // lookup plus a forward walk within the bucket, not just the first anchor.
        using var f = await BuildAsync(ScalarArray(200));

        Assert.Equal(200, f.Elements.ElementCount);
        Assert.Equal(65, f.Elements.TokenForElement(64));   // first element of bucket 1
        Assert.Equal(128, f.Elements.TokenForElement(127)); // last element of bucket 1
        Assert.Equal(200, f.Elements.TokenForElement(199));
    }

    [Fact]
    public async Task ObjectElementsPastTheFirstBucket_ResolveThroughTheirOwnAnchor()
    {
        // Same as above but with multi-token elements, so an anchor is genuinely the only way
        // to reach a late element without re-walking the whole array.
        string json = "[" + string.Join(',', Enumerable.Range(0, 150).Select(i => $$"""{"i":{{i}}}""")) + "]";
        using var f = await BuildAsync(json);

        Assert.Equal(150, f.Elements.ElementCount);

        for (int i = 0; i < 150; i++)
        {
            int token = f.Elements.TokenForElement(i);
            Assert.Equal(JsonTokenKind.StartObject, f.Source.GetToken(token).Kind);
            Assert.Equal(0, f.Source.GetToken(token).ParentIndex);
        }
    }

    [Fact]
    public async Task NestedArraysAreNotMistakenForElements()
    {
        // The inner arrays' own EndArray tokens sit between elements. Skipping containers by
        // EndIndex is what keeps the walk from landing on one and treating it as an element -
        // or, worse, as the outer array's terminator.
        using var f = await BuildAsync("[[1,2],[3,4],[5,6]]");

        Assert.Equal(3, f.Elements.ElementCount);
        foreach (int i in Enumerable.Range(0, 3))
            Assert.Equal(JsonTokenKind.StartArray, f.Source.GetToken(f.Elements.TokenForElement(i)).Kind);
    }

    [Fact]
    public async Task TokenForElement_OutOfRange_Throws()
    {
        using var f = await BuildAsync(ScalarArray(3));

        Assert.Throws<ArgumentOutOfRangeException>(() => f.Elements.TokenForElement(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => f.Elements.TokenForElement(-1));
    }

    [Fact]
    public async Task NonArrayRoot_FailsRatherThanWalkingGarbage()
    {
        using var f = await BuildAsync("""{"a":1}""");

        Assert.NotNull(f.Elements.Failure);
        Assert.True(f.Elements.IsComplete);
        Assert.Equal(0, f.Elements.ElementCount);
    }

    [Fact]
    public async Task TruncatedArray_KeepsTheElementsThatDidClose()
    {
        // The source index fails partway; the elements that closed before the failure are still
        // a perfectly good table, which is the whole reason this index streams instead of
        // waiting for its source to complete.
        string path = System.IO.Path.GetTempFileName();
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("[1,2,3,{\"a\":"));
        try
        {
            using var file = new MMapFile(path);
            var source = JsonStructureIndex.StartIndexing(file);
            var elements = JsonArrayElementIndex.Start(source, 0);

            await Assert.ThrowsAnyAsync<Exception>(() => source.IndexingTask);
            await elements.IndexingTask;

            Assert.Equal(3, elements.ElementCount);
            Assert.Null(elements.Failure); // the failure is the SOURCE's to report, not this one's
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task NestedArray_CanBeIndexedByItsOwnTokenIndex()
    {
        // Not what the table view does today (its mapping IS the array, so the root is token 0),
        // but the type takes an array token index rather than assuming the root, and that has to
        // actually work.
        using var f = await BuildAsync("""{"items":[10,20,30]}""", arrayTokenIndex: 1);

        Assert.Equal(3, f.Elements.ElementCount);
        Assert.Equal(JsonTokenKind.Number, f.Source.GetToken(f.Elements.TokenForElement(0)).Kind);
        Assert.Equal(2, f.Elements.TokenForElement(0));
    }

    [Fact]
    public async Task WaitForElementCount_CompletesOnceTheTargetIsPublished()
    {
        using var f = await BuildAsync(ScalarArray(100));

        await f.Elements.WaitForElementCountAsync(50);

        Assert.True(f.Elements.ElementCount >= 50);
    }

    [Fact]
    public async Task CancelledBeforeTheScanStarts_StillMarksItselfComplete()
    {
        // Task.Run(body, token) SKIPS the body outright when the token is already cancelled as
        // the pool dequeues the work item. That would leave MarkComplete uncalled, IsComplete
        // false forever, and every waiter hanging for the life of the process - which is exactly
        // what a document disposed between starting its scan and the pool picking it up does.
        // AppendLogIndexBase.StartScan is what makes the completion signal unconditional.
        string path = System.IO.Path.GetTempFileName();
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(ScalarArray(200)));
        try
        {
            using var file = new MMapFile(path);
            var source = JsonStructureIndex.StartIndexing(file);

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var elements = JsonArrayElementIndex.Start(source, 0, cts.Token);

            await Observed(elements.IndexingTask);
            Assert.True(elements.IsComplete);

            // The assertion that matters: a waiter is released rather than hanging forever. A
            // regression here would hang the run, so it is raced against a timeout.
            var wait = elements.WaitForElementCountAsync(10);
            Assert.Same(wait, await Task.WhenAny(wait, Task.Delay(5000)));

            await Observed(source.IndexingTask);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task WaitForElementCount_UnreachableTarget_IsReleasedByCompletion()
    {
        using var f = await BuildAsync(ScalarArray(3));

        await f.Elements.WaitForElementCountAsync(1000);

        Assert.Equal(3, f.Elements.ElementCount);
    }
}
