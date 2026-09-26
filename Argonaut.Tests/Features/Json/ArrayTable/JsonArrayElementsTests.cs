using System.Text;
using System.Text.Json;
using Argonaut.Engine.Bytes;
using Argonaut.Features.Json.ArrayTable;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Json.Tree;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Features.Json.ArrayTable;

/// <summary>
/// Element addressing for the array table: ordinal to element over the sparse index, checked
/// against <c>Utf8JsonReader</c> - every element kind, arrays long enough to go through recorded
/// containers and checkpoints, and a still-arriving array that only ever counts elements that have
/// ended.
/// </summary>
public class JsonArrayElementsTests
{
    private static JsonArrayElements Build(IByteSource source, out JsonSparseIndex index)
    {
        index = JsonSparseIndex.StartIndexing(source);
        try
        {
            index.IndexingTask.GetAwaiter().GetResult();
        }
        catch
        {
            // Malformed input is some tests' subject; they read what was indexed.
        }

        var reader = new JsonTreeReader(source);
        return new JsonArrayElements(index, reader, new JsonTreeText(source, index.Structure, reader));
    }

    /// <summary>Each root-array element's start, from the reader.</summary>
    private static List<long> ElementStarts(byte[] json)
    {
        var starts = new List<long>();
        var reader = new Utf8JsonReader(json);
        reader.Read();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            starts.Add(reader.TokenStartIndex);
            reader.Skip();
        }

        return starts;
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[1]")]
    [InlineData("[1, \"two\", null, true, {\"a\":[1,2]}, [3,[4]], {}, []]")]
    public void SmallArraysAddressEveryElementInOrder(string text)
    {
        byte[] json = Encoding.UTF8.GetBytes(text);
        var elements = Build(new MemoryByteSource(json), out _);
        var expected = ElementStarts(json);

        Assert.Equal(expected.Count, elements.ElementCount);
        for (int i = 0; i < expected.Count; i++)
            Assert.Equal(expected[i], elements.ElementAt(i).ValueStart);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void LargeArraysResolveThroughTheIndexInAnyOrder(int seed)
    {
        byte[] json = RandomJson.LargeContainers(new Random(seed), elements: 3000);
        var elements = Build(new MemoryByteSource(json), out var index);
        var expected = ElementStarts(json);

        Assert.True(index.Structure.CheckpointCount > 2, "the array should be large enough to have checkpoints");
        Assert.Equal(expected.Count, elements.ElementCount);

        // Backwards and scattered, so the read-ahead cache cannot carry every answer.
        var random = new Random(seed);
        for (int i = expected.Count - 1; i >= 0; i -= random.Next(1, 40))
            Assert.Equal(expected[i], elements.ElementAt(i).ValueStart);
        for (int i = 0; i < expected.Count; i += random.Next(1, 40))
            Assert.Equal(expected[i], elements.ElementAt(i).ValueStart);
    }

    [Fact]
    public void OutOfRangeThrows()
    {
        var elements = Build(new MemoryByteSource("[1,2]"u8.ToArray()), out _);

        Assert.Throws<ArgumentOutOfRangeException>(() => elements.ElementAt(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => elements.ElementAt(-1));
    }

    [Fact]
    public void ANonArrayDocumentHasNoElements()
    {
        var elements = Build(new MemoryByteSource("{\"a\":1}"u8.ToArray()), out _);

        Assert.Null(elements.Array);
        Assert.Equal(0, elements.ElementCount);
    }

    [Fact]
    public void ATruncatedArrayKeepsEveryElementThatBegan()
    {
        byte[] json = "[1, 2, {\"a\": [3, 4"u8.ToArray();
        var elements = Build(new MemoryByteSource(json), out var index);

        Assert.NotNull(index.Failure);
        Assert.Equal(3, elements.ElementCount);
        Assert.Equal(json.AsSpan().IndexOf((byte)'{'), elements.ElementAt(2).ValueStart);
    }

    [Fact]
    public async Task WhileArrivingOnlyElementsThatHaveEndedAreCounted()
    {
        byte[] json = Encoding.UTF8.GetBytes("[" + string.Join(",", Enumerable.Range(0, 60_000).Select(i => $"{{\"i\":{i}}}")) + "]");
        var expected = ElementStarts(json);
        var growing = new GrowingByteSource(json, initiallyAvailable: json.Length / 2);
        var index = JsonSparseIndex.StartIndexing(growing);
        var reader = new JsonTreeReader(growing);
        var elements = new JsonArrayElements(index, reader, new JsonTreeText(growing, index.Structure, reader));

        await elements.WaitForElementCountAsync(1);
        int partway = elements.ElementCount;
        Assert.InRange(partway, 1, expected.Count - 1);
        for (int i = 0; i < partway; i += 997)
            Assert.Equal(expected[i], elements.ElementAt(i).ValueStart);

        growing.Reveal(json.Length);
        growing.Seal();
        await index.IndexingTask;
        Assert.Equal(expected.Count, elements.ElementCount);
    }
}
