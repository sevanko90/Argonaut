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

    [Theory]
    [InlineData(4_000_000_000L, 4_000_200_000L, "4g", "4.0002g")]
    [InlineData(4_000_000_000L, 4_020_000_000L, "4g", "4.02g")]
    [InlineData(1_000_000L, 1_000_050L, "1m", "1.0001m")]
    [InlineData(1_000_000L, 2_000_000L, "1m", "2m")]
    [InlineData(100_000L, 100_100L, "100k", "100.1k")]
    [InlineData(5_000L, 5_000L, "5,000", "5,000")]
    [InlineData(2_000_000_000L, 2_000_000_000L, "2g", "2g")]
    [InlineData(4_000_000_000L, 4_000_000_499L, "4,000,000,000", "4,000,000,499")]
    public void A_ranges_ends_read_differently_unless_they_are_the_same(long first, long last, string expectedFirst, string expectedLast)
    {
        var culture = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal((expectedFirst, expectedLast), CompactCount.FormatRange(first, last));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = culture;
        }
    }
}
