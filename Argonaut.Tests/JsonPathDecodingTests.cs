using System.Text;
using Argonaut.Features.Json;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

public class JsonPathDecodingTests
{
    [Theory]
    [InlineData("\\u0061", "a")]
    [InlineData("a\\\"b", "a\"b")]
    [InlineData("a\\\\b", "a\\b")]
    [InlineData("\\uD83D\\uDE00", "😀")]
    [InlineData("a\\nb", "a\nb")]
    [InlineData("\\u00e9", "é")]
    public async Task DecodedNamesResolveAndSelectedPathsRoundTrip(string serializedName, string decodedName)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"" + serializedName + "\":1}");
            using var session = IndexedFileSession<JsonStructureIndex>.Start(new MMapFile(path), JsonStructureIndex.StartIndexing);
            await session.IndexingTask;
            string expectedPath = "$['" + decodedName.Replace("\\", "\\\\").Replace("'", "\\'") + "']";
            var resolved = await JsonPathResolver.ResolveAsync(session.Index, session.File, expectedPath);
            Assert.Equal(1, resolved.TokenIndex);
            string selectedPath = JsonPathBuilder.Build(session.Index, session.File, 1);
            Assert.Equal(decodedName == "a" ? "$.a" : expectedPath, selectedPath);
            Assert.Equal(1, (await JsonPathResolver.ResolveAsync(session.Index, session.File, selectedPath)).TokenIndex);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EscapedComparisonHasNoPerNameAllocationEvenForLargeNames()
    {
        byte[] serialized = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("\\u0061", 100_000)));
        byte[] decoded = Encoding.UTF8.GetBytes(new string('a', 100_000));
        Assert.True(JsonUnescape.EqualsDecodedUtf8(serialized, decoded));
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool matches = JsonUnescape.EqualsDecodedUtf8(serialized, decoded);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(matches);
        Assert.Equal(0, allocated);
        char[] characters = new char[100_000];
        before = GC.GetAllocatedBytesForCurrentThread();
        int count = JsonUnescape.DecodeUtf16(serialized, default);
        int written = JsonUnescape.DecodeUtf16(serialized, characters);
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(100_000, count);
        Assert.Equal(count, written);
        Assert.Equal('a', characters[^1]);
        Assert.Equal(0, allocated);
        decoded[^1] = (byte)'b';
        Assert.False(JsonUnescape.EqualsDecodedUtf8(serialized, decoded));
    }

    [Theory]
    [InlineData("\\u0061", "\\u0061")]
    [InlineData("abc", "ab")]
    [InlineData("\\u0061b", "a")]
    public void SerializedSpellingsAreNotDecodedNames(string serialized, string decoded)
        => Assert.False(JsonUnescape.EqualsDecodedUtf8(Encoding.UTF8.GetBytes(serialized), Encoding.UTF8.GetBytes(decoded)));
}
