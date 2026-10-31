using Argonaut.Engine.Detection;

namespace Argonaut.Tests.Engine.Detection;

public class LineEndingTallyTests
{
    [Theory]
    [InlineData(5, 0, 0, LineEndingStyle.Lf)]
    [InlineData(0, 5, 0, LineEndingStyle.CrLf)]
    [InlineData(0, 0, 5, LineEndingStyle.Cr)]
    [InlineData(10, 2, 0, LineEndingStyle.Lf)]      // mixed: the most common
    [InlineData(1, 9, 3, LineEndingStyle.CrLf)]
    [InlineData(4, 4, 0, null)]                     // a tie gives nothing to follow
    [InlineData(0, 0, 0, null)]                     // so does a document with no breaks
    public void Prevailing_IsWhatANewLineFollows(long lf, long crLf, long loneCr, LineEndingStyle? expected)
        => Assert.Equal(expected, new LineEndingTally(lf, crLf, loneCr, 100, true).Prevailing);
}
