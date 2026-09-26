using System.Text;
using Argonaut.Features.Json.Indexing;

namespace Argonaut.Tests.Features.Json.Indexing;

/// <summary>
/// Comparing a name as written against decoded text, the way a path looks a member up: no
/// allocation however long the name, and a spelling is never mistaken for what it decodes to.
/// </summary>
public class JsonUnescapeTests
{
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
