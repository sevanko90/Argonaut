using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Text;

namespace Argonaut.Tests.Engine.Text;

public class Utf8BoundaryTests
{
    private static long StartAtOrBefore(string text, long offset)
        => Utf8Boundary.CharacterStartAtOrBefore(new MemoryByteSource(Encoding.UTF8.GetBytes(text)), offset);

    [Theory]
    [InlineData("abc", 1, 1)]
    [InlineData("ab€cd", 2, 2)] // € occupies 2..4
    [InlineData("ab€cd", 3, 2)]
    [InlineData("ab€cd", 4, 2)]
    [InlineData("ab€cd", 5, 5)]
    [InlineData("a😀b", 4, 1)] // 😀 occupies 1..4
    [InlineData("€", 0, 0)]
    public void Moves_back_to_the_start_of_the_character(string text, long offset, long expected)
        => Assert.Equal(expected, StartAtOrBefore(text, offset));

    [Fact]
    public void Never_moves_back_more_than_three_bytes()
    {
        byte[] continuationBytes = [0x41, 0x80, 0x80, 0x80, 0x80, 0x80];

        Assert.Equal(2, Utf8Boundary.CharacterStartAtOrBefore(new MemoryByteSource(continuationBytes), 5));
    }
}
