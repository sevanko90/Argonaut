using Argonaut.Ui.TableGrid;

namespace Argonaut.Tests.Ui.TableGrid;

/// <summary>
/// Verifies <see cref="TableColumnLayout"/>: edges add up, an x finds its column, a stretch of
/// the page finds the columns it covers, an edge is found within its tolerance, and a width the
/// user set survives a reseed while the others move.
/// </summary>
public class TableColumnLayoutTests
{
    private static TableColumnLayout Layout() => new([100, 50, 200, 30]);

    [Fact]
    public void Edges_add_up()
    {
        var layout = Layout();

        Assert.Equal(380, layout.TotalWidth);
        Assert.Equal(150, layout.Left(2));
        Assert.Equal(350, layout.Right(2));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(99.9, 0)]
    [InlineData(100, 1)]
    [InlineData(349, 2)]
    [InlineData(379, 3)]
    [InlineData(380, -1)]
    [InlineData(-1, -1)]
    public void An_x_is_in_one_column(double x, int column) => Assert.Equal(column, Layout().ColumnAt(x));

    [Theory]
    [InlineData(0, 100, 0, 0)]
    [InlineData(90, 160, 0, 2)]
    [InlineData(120, 1_000, 1, 3)]
    [InlineData(-50, 10, 0, 0)]
    public void A_stretch_covers_the_columns_it_touches(double from, double to, int first, int last)
        => Assert.Equal((first, last), Layout().Covering(from, to));

    [Fact]
    public void A_stretch_past_the_columns_covers_none()
    {
        var (first, last) = Layout().Covering(400, 600);
        Assert.True(last < first);
    }

    [Theory]
    [InlineData(102, 0)]
    [InlineData(147, 1)]
    [InlineData(380, 3)]
    [InlineData(125, -1)]
    public void Finds_the_edge_under_the_pointer(double x, int column) => Assert.Equal(column, Layout().EdgeNear(x, 4));

    [Fact]
    public void A_width_the_user_set_survives_a_reseed()
    {
        var layout = Layout();
        layout.SetUserWidth(1, 80, minimum: 20);
        layout.SetUserWidth(3, 5, minimum: 20);

        layout.Reseed([110, 55, 210, 33]);

        Assert.Equal([110, 80, 210, 20], Enumerable.Range(0, 4).Select(layout.Width));
        Assert.True(layout.IsUserSized(1));
        Assert.False(layout.IsUserSized(0));
        Assert.Equal(420, layout.TotalWidth);
    }
}
