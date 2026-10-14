using Argonaut.Engine.Text;

namespace Argonaut.Tests.Engine.Text;

public class CompactCountTests
{
    [Theory]
    [InlineData(0L, "0")]
    [InlineData(9_999L, "9,999")]
    [InlineData(10_000L, "10k")]
    [InlineData(123_456L, "123k")]
    [InlineData(100_000L, "100k")]
    [InlineData(999_999L, "1m")]
    [InlineData(1_000_000L, "1m")]
    [InlineData(1_500_000L, "1.5m")]
    [InlineData(2_999_999L, "3m")]
    [InlineData(12_345_678L, "12.3m")]
    [InlineData(1_600_000_000L, "1.6g")]
    [InlineData(2_450_000_000_000L, "2450g")]
    public void Rounds_to_three_figures_with_the_command_lines_multipliers(long count, string expected)
    {
        var culture = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(expected, CompactCount.Format(count));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = culture;
        }
    }
}
