using System.Text;
using Argonaut.Features.Json;

namespace Argonaut.Tests;

/// <summary>
/// Verifies ExpandedRoutes on its own: matching a child by raw property name or by array
/// position, the levels Build lays out for nested routes, and the answer that matters most for
/// the grid's cost - "nothing below this child is expanded", which is what lets a realized row
/// skip a subtree whole.
/// </summary>
public class JsonArrayColumnRoutesTests
{
    private static ReadOnlySpan<byte> Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void ForProperties_DrawsEachNameInItsOwnColumnAndExpandsNothing()
    {
        var routes = ExpandedRoutes.ForProperties(["id", "name"]);

        Assert.True(routes.TryMatchName(Utf8("name"), out int column, out var inner));
        Assert.Equal(1, column);
        Assert.Null(inner);
    }

    [Fact]
    public void UnknownName_DoesNotMatch()
    {
        var routes = ExpandedRoutes.ForProperties(["id"]);

        Assert.False(routes.TryMatchName(Utf8("missing"), out int column, out var inner));
        Assert.Equal(-1, column);
        Assert.Null(inner);
    }

    [Fact]
    public void NamesMatchRawBytes_NotDecodedText()
    {
        // The column was discovered from the raw name, escape sequence and all, so that is what
        // the mapping is matched against - decoding either side would make the two disagree.
        var routes = ExpandedRoutes.ForProperties([@"a\u0062c"]);

        Assert.True(routes.TryMatchName(Utf8(@"a\u0062c"), out _, out _));
        Assert.False(routes.TryMatchName(Utf8("abc"), out _, out _));
    }

    [Fact]
    public void ExpandedContainer_DrawsNoColumnItselfButOffersALevelToDescendInto()
    {
        var routes = ExpandedRoutes.Build([
            ColumnRoute.Property("id"),
            new ColumnRoute([RouteStep.Property("geometry"), RouteStep.Property("type")], "geometry.type"),
        ]);

        Assert.True(routes.TryMatchName(Utf8("geometry"), out int column, out var inner));
        Assert.Equal(-1, column); // the container is expanded, so it has no cell of its own
        Assert.NotNull(inner);

        Assert.True(inner!.TryMatchName(Utf8("type"), out int inside, out var deeper));
        Assert.Equal(1, inside);
        Assert.Null(deeper);
    }

    [Fact]
    public void SiblingRoutesThroughOneContainer_ShareASingleLevel()
    {
        var routes = ExpandedRoutes.Build([
            new ColumnRoute([RouteStep.Property("geometry"), RouteStep.Property("type")], "geometry.type"),
            new ColumnRoute([RouteStep.Property("geometry"), RouteStep.At(0)], "geometry[0]"),
        ]);

        Assert.True(routes.TryMatchName(Utf8("geometry"), out _, out var inner));
        Assert.True(inner!.TryMatchName(Utf8("type"), out int type, out _));
        Assert.True(inner.TryMatchIndex(0, out int first, out _));
        Assert.Equal(0, type);
        Assert.Equal(1, first);
    }

    [Fact]
    public void ArrayPositions_MatchByIndexAndOnlyTheOnesExpanded()
    {
        var routes = ExpandedRoutes.Build([
            new ColumnRoute([RouteStep.Property("bbox"), RouteStep.At(0)], "bbox[0]"),
            new ColumnRoute([RouteStep.Property("bbox"), RouteStep.At(1)], "bbox[1]"),
        ]);

        Assert.True(routes.TryMatchName(Utf8("bbox"), out _, out var inner));
        Assert.True(inner!.TryMatchIndex(1, out int second, out _));
        Assert.Equal(1, second);

        // The cap is what keeps a 7,982-element array finite: positions past the expanded ones
        // simply have no column.
        Assert.False(inner.TryMatchIndex(2, out _, out _));
    }

    [Fact]
    public void None_MatchesNothing()
    {
        Assert.True(ExpandedRoutes.None.IsEmpty);
        Assert.False(ExpandedRoutes.None.TryMatchName(Utf8("id"), out _, out _));
        Assert.False(ExpandedRoutes.None.TryMatchIndex(0, out _, out _));
    }
}
