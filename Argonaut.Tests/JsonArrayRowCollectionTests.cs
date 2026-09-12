using System.Text;
using Argonaut.Features.Csv;
using Argonaut.Features.Json;

namespace Argonaut.Tests;

/// <summary>
/// Verifies JsonArrayRowCollection: element-to-row mapping in both column modes, cell text
/// matching what the tree shows for the same token (quotes included), ragged and non-object
/// elements, and SetShape re-chunking without re-walking. Indexing always runs to completion
/// before construction so the growth-monitor path (which needs an Avalonia dispatcher) never
/// starts - the same discipline CsvRowCollectionTests uses.
/// </summary>
public class JsonArrayRowCollectionTests
{
    private static async Task WithRows(string json, CsvStructure structure, JsonArrayColumnMode mode,
        Action<JsonArrayRowCollection> assert)
    {
        string path = Path.GetTempFileName();
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(json));
        try
        {
            using var session = JsonArrayTableSession.Start(path, 0, new FileInfo(path).Length);
            await session.IndexingTask;

            using var rows = new JsonArrayRowCollection(session.Elements, session.Inner.Index, session.Inner.Bytes,
                structure, RoutesFor(structure, mode), mode);
            assert(rows);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static CsvStructure Columns(params string[] names)
        => CsvStructure.FromMaxChars(names, names.Select(n => n.Length).ToArray());

    /// <summary>The flat routes discovery would have produced for these columns - one direct
    /// property step each - and none at all for a reshape, whose cells are whole elements.</summary>
    private static ExpandedRoutes RoutesFor(CsvStructure structure, JsonArrayColumnMode mode)
        => mode == JsonArrayColumnMode.ByProperty
            ? ExpandedRoutes.ForProperties(structure.Columns.Select(c => c.Name).ToArray())
            : ExpandedRoutes.None;

    /// <summary>The same harness with routes chosen by the caller - what a header expansion
    /// will hand the collection once slice 2 can build one.</summary>
    private static async Task WithRoutedRows(string json, CsvStructure structure, ExpandedRoutes routes,
        Action<JsonArrayRowCollection> assert)
    {
        string path = Path.GetTempFileName();
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(json));
        try
        {
            using var session = JsonArrayTableSession.Start(path, 0, new FileInfo(path).Length);
            await session.IndexingTask;

            using var rows = new JsonArrayRowCollection(session.Elements, session.Inner.Index, session.Inner.Bytes,
                structure, routes, JsonArrayColumnMode.ByProperty);
            assert(rows);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string[] TextOf(JsonArrayRowCollection rows, int i)
        => ((CsvVisibleRow)rows[i]!).Cells.Select(c => c.Text).ToArray();

    [Fact]
    public Task ByProperty_OneRowPerElementWithCellsInColumnOrder()
        => WithRows("""[{"id":1,"name":"alpha"},{"id":2,"name":"beta"}]""", Columns("id", "name"), JsonArrayColumnMode.ByProperty, rows =>
        {
            Assert.Equal(2, rows.Count);
            Assert.Equal(["1", "\"alpha\""], TextOf(rows, 0));
            Assert.Equal(["2", "\"beta\""], TextOf(rows, 1));
        });

    [Fact]
    public Task ByProperty_PropertiesLandInTheirColumnRegardlessOfDocumentOrder()
        => WithRows("""[{"id":1,"name":"alpha"},{"name":"beta","id":2}]""", Columns("id", "name"), JsonArrayColumnMode.ByProperty, rows =>
        {
            Assert.Equal(["2", "\"beta\""], TextOf(rows, 1));
        });

    [Fact]
    public Task ByProperty_MissingPropertyLeavesAnEmptyCell()
        => WithRows("""[{"id":1,"name":"alpha"},{"id":2}]""", Columns("id", "name"), JsonArrayColumnMode.ByProperty, rows =>
        {
            Assert.Equal(["2", ""], TextOf(rows, 1));
        });

    [Fact]
    public Task ByProperty_UnknownPropertyHasNoColumnAndIsDropped()
        => WithRows("""[{"id":1},{"id":2,"extra":9}]""", Columns("id"), JsonArrayColumnMode.ByProperty, rows =>
        {
            Assert.Equal(["2"], TextOf(rows, 1));
        });

    [Fact]
    public Task ByProperty_NestedContainersShowASummaryNotTheirContents()
        => WithRows("""[{"id":1,"tags":["a","b","c"],"meta":{"x":1}}]""", Columns("id", "tags", "meta"), JsonArrayColumnMode.ByProperty, rows =>
        {
            Assert.Equal(["1", "[ 3 items ]", "{ 1 member }"], TextOf(rows, 0));
        });

    [Fact]
    public Task ByProperty_NestedPropertiesDoNotLeakIntoTheirAncestorsColumns()
        => WithRows("""[{"id":1,"meta":{"id":99}}]""", Columns("id", "meta"), JsonArrayColumnMode.ByProperty, rows =>
        {
            // The walk skips each nested container whole, so the inner "id" never overwrites
            // the element's own.
            Assert.Equal(["1", "{ 1 member }"], TextOf(rows, 0));
        });

    [Fact]
    public Task ByProperty_NonObjectElementRendersAsOneCell()
        => WithRows("""[{"id":1},42]""", Columns("id"), JsonArrayColumnMode.ByProperty, rows =>
        {
            Assert.Equal(["42"], TextOf(rows, 1));
        });

    [Fact]
    public Task ScalarArray_RendersOneValueColumn()
        => WithRows("""["a",1,true,null]""", Columns("value"), JsonArrayColumnMode.ByProperty, rows =>
        {
            Assert.Equal(4, rows.Count);
            Assert.Equal(["\"a\""], TextOf(rows, 0));
            Assert.Equal(["1"], TextOf(rows, 1));
            Assert.Equal(["true"], TextOf(rows, 2));
            Assert.Equal(["null"], TextOf(rows, 3));
        });

    [Fact]
    public Task StringCellsKeepTheirQuotes()
        => WithRows("""["5",5]""", Columns("value"), JsonArrayColumnMode.ByProperty, rows =>
        {
            // "5" and 5 are different data; a table that renders them identically is lying
            // about the document.
            Assert.Equal(["\"5\""], TextOf(rows, 0));
            Assert.Equal(["5"], TextOf(rows, 1));
        });

    [Fact]
    public Task Reshape_ChunksElementsRowMajor()
        => WithRows("[1,2,3,4,5,6]", Columns("Column 1", "Column 2"), JsonArrayColumnMode.Reshape, rows =>
        {
            Assert.Equal(3, rows.Count);
            Assert.Equal(["1", "2"], TextOf(rows, 0));
            Assert.Equal(["3", "4"], TextOf(rows, 1));
            Assert.Equal(["5", "6"], TextOf(rows, 2));
        });

    [Fact]
    public Task Reshape_PartialLastRowYieldsFewerCells()
        => WithRows("[1,2,3,4,5]", Columns("Column 1", "Column 2"), JsonArrayColumnMode.Reshape, rows =>
        {
            Assert.Equal(3, rows.Count);
            Assert.Equal(["5"], TextOf(rows, 2)); // no padding cell, and nothing misaligns
        });

    [Fact]
    public Task Reshape_ObjectElementsShowAContainerSummaryPerCell()
        => WithRows("""[{"a":1},{"a":2},{"a":3},{"a":4}]""", Columns("Column 1", "Column 2"), JsonArrayColumnMode.Reshape, rows =>
        {
            Assert.Equal(2, rows.Count);
            Assert.Equal(["{ 1 member }", "{ 1 member }"], TextOf(rows, 0));
        });

    [Fact]
    public Task SetShape_ReChunksWithoutReWalking()
        => WithRows("[1,2,3,4,5,6]", Columns("value"), JsonArrayColumnMode.ByProperty, rows =>
        {
            Assert.Equal(6, rows.Count);

            rows.SetShape(Columns("Column 1", "Column 2", "Column 3"), ExpandedRoutes.None, JsonArrayColumnMode.Reshape);

            Assert.Equal(2, rows.Count);
            Assert.Equal(["1", "2", "3"], TextOf(rows, 0));
            Assert.Equal(["4", "5", "6"], TextOf(rows, 1));
        });

    [Fact]
    public Task SetShape_RaisesResetSoRealizedRowsAreRebuilt()
        => WithRows("[1,2,3,4]", Columns("value"), JsonArrayColumnMode.ByProperty, rows =>
        {
            _ = rows[0]; // realize and cache a row under the old shape
            System.Collections.Specialized.NotifyCollectionChangedEventArgs? captured = null;
            rows.CollectionChanged += (_, e) => captured = e;

            rows.SetShape(Columns("Column 1", "Column 2"), ExpandedRoutes.None, JsonArrayColumnMode.Reshape);

            Assert.NotNull(captured);
            Assert.Equal(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, captured!.Action);
            Assert.Equal(["1", "2"], TextOf(rows, 0));
        });

    [Fact]
    public Task SetShape_SameShape_DoesNotNotify()
        => WithRows("[1,2,3,4]", Columns("value"), JsonArrayColumnMode.ByProperty, rows =>
        {
            var structure = Columns("value");
            var routes = RoutesFor(structure, JsonArrayColumnMode.ByProperty);
            rows.SetShape(structure, routes, JsonArrayColumnMode.ByProperty);

            bool raised = false;
            rows.CollectionChanged += (_, _) => raised = true;
            rows.SetShape(structure, routes, JsonArrayColumnMode.ByProperty);

            Assert.False(raised);
        });

    [Fact]
    public Task OutOfRangeIndex_ReturnsAnEmptyRowInsteadOfThrowing()
        => WithRows("[1,2]", Columns("value"), JsonArrayColumnMode.ByProperty, rows =>
        {
            Assert.Empty(((CsvVisibleRow)rows[10]!).Cells);
        });

    [Fact]
    public Task RepeatedAccess_ReturnsTheCachedRow()
        => WithRows("[1,2]", Columns("value"), JsonArrayColumnMode.ByProperty, rows =>
        {
            Assert.Same(rows[0], rows[0]);
        });

    [Fact]
    public Task EmptyArray_HasNoRows()
        => WithRows("[]", Columns("value"), JsonArrayColumnMode.ByProperty, Assert.Empty);

    [Fact]
    public Task RowNumbersAreOneBased()
        => WithRows("[1,2,3]", Columns("value"), JsonArrayColumnMode.ByProperty, rows =>
        {
            Assert.Equal(1, ((CsvVisibleRow)rows[0]!).RowNumber);
            Assert.Equal(3, ((CsvVisibleRow)rows[2]!).RowNumber);
        });

    [Fact]
    public Task ExpandedContainer_DrawsItsChildrenIntoTheirOwnColumns()
        => WithRoutedRows("""[{"id":1,"geometry":{"type":"Point","coordinates":[1,2]}}]""",
            Columns("id", "geometry.type", "geometry.coordinates"),
            ExpandedRoutes.Build([
                ColumnRoute.Property("id"),
                new ColumnRoute([RouteStep.Property("geometry"), RouteStep.Property("type")], "geometry.type"),
                new ColumnRoute([RouteStep.Property("geometry"), RouteStep.Property("coordinates")], "geometry.coordinates"),
            ]),
            rows => Assert.Equal(["1", "\"Point\"", "[ 2 items ]"], TextOf(rows, 0)));

    [Fact]
    public Task ExpandedArray_DrawsThePositionsExpandedAndNoMore()
        => WithRoutedRows("""[{"bbox":[10,20,30,40,50,60,70,80]}]""",
            Columns("bbox[0]", "bbox[1]"),
            ExpandedRoutes.Build([
                new ColumnRoute([RouteStep.Property("bbox"), RouteStep.At(0)], "bbox[0]"),
                new ColumnRoute([RouteStep.Property("bbox"), RouteStep.At(1)], "bbox[1]"),
            ]),
            // The eight-element array costs two cells: this is what keeps
            // $.features[7].geometry.coordinates[7982] finite.
            rows => Assert.Equal(["10", "20"], TextOf(rows, 0)));

    [Fact]
    public Task ExpansionNestsAsDeepAsTheRouteAsksAndNoDeeper()
        => WithRoutedRows("""[{"a":{"b":{"c":1,"d":{"e":2}}}}]""",
            Columns("a.b.c", "a.b.d"),
            ExpandedRoutes.Build([
                new ColumnRoute([RouteStep.Property("a"), RouteStep.Property("b"), RouteStep.Property("c")], "a.b.c"),
                new ColumnRoute([RouteStep.Property("a"), RouteStep.Property("b"), RouteStep.Property("d")], "a.b.d"),
            ]),
            // "d" is drawn as a summary, not walked into - nothing below it was asked for.
            rows => Assert.Equal(["1", "{ 1 member }"], TextOf(rows, 0)));

    [Fact]
    public Task ElementMissingTheExpandedRoute_LeavesThoseCellsEmpty()
        => WithRoutedRows("""[{"geometry":{"type":"Point"}},{"id":2},{"geometry":{}}]""",
            Columns("id", "geometry.type"),
            ExpandedRoutes.Build([
                ColumnRoute.Property("id"),
                new ColumnRoute([RouteStep.Property("geometry"), RouteStep.Property("type")], "geometry.type"),
            ]),
            rows =>
            {
                Assert.Equal(["", "\"Point\""], TextOf(rows, 0));
                Assert.Equal(["2", ""], TextOf(rows, 1));
                Assert.Equal(["", ""], TextOf(rows, 2));
            });

    [Fact]
    public Task ScalarWhereAContainerWasExpected_IsDrawnRatherThanDescendedInto()
        => WithRoutedRows("""[{"geometry":"Point"}]""",
            Columns("geometry.type"),
            ExpandedRoutes.Build([
                new ColumnRoute([RouteStep.Property("geometry"), RouteStep.Property("type")], "geometry.type"),
            ]),
            // Ragged data, not an error: the route passes through a scalar, so it draws nothing
            // and the walk moves on rather than reading EndIndex off a scalar token.
            rows => Assert.Equal([""], TextOf(rows, 0)));
}
