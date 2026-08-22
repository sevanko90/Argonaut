using System.Text.Json;
using Argonaut.Features.Json;
using Argonaut.Infrastructure;
using BenchmarkDotNet.Attributes;

namespace Argonaut.Tests;

/// <summary>
/// Measures the time/allocation cost of one <see cref="JsonVisibleRowCollection.Rebuild"/>
/// (triggered indirectly via <see cref="JsonVisibleRowCollection.ToggleExpand"/> on an
/// unrelated container) while a top-level 10-element array has elements 3, 5 and 9 expanded
/// - each a nested array large enough to exercise both the initial reveal and one "show more"
/// page. The parameters reference the production constants so cap changes cannot silently
/// make the benchmark measure a mislabeled size or search for a nonexistent placeholder.
/// </summary>
[MemoryDiagnoser]
public class RebuildCostBenchmarks
{
    private const int OuterCount = 10;
    private const int InnerCount = JsonVisibleRowCollection.MaxDisplayedChildrenPerContainer;
    private static readonly int[] ExpandedElements = { 3, 5, 9 };

    [Params(JsonVisibleRowCollection.ChildCap, JsonVisibleRowCollection.MaxDisplayedChildrenPerContainer)]
    public int RevealLimit { get; set; }

    private string path = null!;
    private MMapFile mmap = null!;
    private JsonStructureIndex index = null!;
    private JsonVisibleRowCollection rows = null!;
    private int triggerPosition;

    [GlobalSetup]
    public void Setup()
    {
        path = Path.Combine(Path.GetTempPath(), $"bench-{Guid.NewGuid():N}.json");
        WriteSyntheticJson(path, OuterCount, InnerCount);

        mmap = new MMapFile(path);
        index = JsonStructureIndex.StartIndexing(mmap);
        index.IndexingTask.GetAwaiter().GetResult();

        rows = new JsonVisibleRowCollection(index, mmap);

        int dataArrayToken = FindDataArrayToken(index);
        int dataPos = rows.FindVisiblePosition(dataArrayToken)
            ?? throw new InvalidOperationException("data array not visible");
        rows.ToggleExpand(dataPos); // reveal the 10 elements

        var elementTokens = FindDirectArrayChildren(index, dataArrayToken);
        foreach (int elementIndex in ExpandedElements)
            ExpandElementToLimit(rows, elementTokens[elementIndex], RevealLimit);

        int triggerToken = FindTriggerToken(index);
        triggerPosition = rows.FindVisiblePosition(triggerToken)
            ?? throw new InvalidOperationException("trigger object not visible");
    }

    [Benchmark]
    public void Rebuild_WithThreeLargeArraysExpanded()
    {
        // Not a container-content change - flips an unrelated empty object's expand
        // state, so every call takes Rebuild's full re-walk path (not the pure-append
        // fast path) while the three big arrays stay exactly as expanded.
        rows.ToggleExpand(triggerPosition);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        mmap.Dispose();
        File.Delete(path);
    }

    private static void ExpandElementToLimit(JsonVisibleRowCollection rows, int arrayTokenIndex, int revealLimit)
    {
        int pos = rows.FindVisiblePosition(arrayTokenIndex)
            ?? throw new InvalidOperationException("element array not visible");
        rows.ToggleExpand(pos); // reveals the first production-sized page
        int revealed = JsonVisibleRowCollection.ChildCap;

        while (revealed < revealLimit)
        {
            int placeholderPos = FindPlaceholderPosition(rows);
            rows.ToggleExpand(placeholderPos); // "show more": bumps the limit by ChildCap
            revealed += JsonVisibleRowCollection.ChildCap;
        }
    }

    private static int FindPlaceholderPosition(JsonVisibleRowCollection rows)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i] is JsonRow { IsPlaceholder: true })
                return i;
        }

        throw new InvalidOperationException("expected a 'more items' placeholder row");
    }

    private static int FindDataArrayToken(JsonStructureIndex index)
    {
        for (int i = 0; i < index.TokenCount; i++)
        {
            var token = index.GetToken(i);
            if (token.Kind == JsonTokenKind.StartArray && token.ParentIndex == 0)
                return i;
        }

        throw new InvalidOperationException("data array not found");
    }

    private static List<int> FindDirectArrayChildren(JsonStructureIndex index, int parentIndex)
    {
        var result = new List<int>();
        for (int i = 0; i < index.TokenCount; i++)
        {
            var token = index.GetToken(i);
            if (token.Kind == JsonTokenKind.StartArray && token.ParentIndex == parentIndex)
                result.Add(i);
        }

        return result;
    }

    private static int FindTriggerToken(JsonStructureIndex index)
    {
        for (int i = 0; i < index.TokenCount; i++)
        {
            var token = index.GetToken(i);
            if (token.Kind == JsonTokenKind.StartObject && token.ParentIndex == 0)
                return i;
        }

        throw new InvalidOperationException("trigger object not found");
    }

    private static void WriteSyntheticJson(string path, int outerCount, int innerCount)
    {
        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });

        writer.WriteStartObject();

        writer.WriteStartArray("data");
        for (int i = 0; i < outerCount; i++)
        {
            writer.WriteStartArray();
            for (int n = 0; n < innerCount; n++)
                writer.WriteNumberValue(n);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();

        writer.WriteStartObject("__trigger");
        writer.WriteEndObject();

        writer.WriteEndObject();
        writer.Flush();
    }
}
