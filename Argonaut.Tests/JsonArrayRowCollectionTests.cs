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

            using var rows = new JsonArrayRowCollection(session.Elements, session.Inner.Index, session.Inner.File, structure, mode);
            assert(rows);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static CsvStructure Columns(params string[] names)
        => CsvStructure.FromMaxChars(names, names.Select(n => n.Length).ToArray());

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
    public Task CellWidthsComeFromTheStructure()
        => WithRows("[1,2]", CsvStructure.FromMaxChars(["Column 1", "Column 2"], [40, 8]), JsonArrayColumnMode.Reshape, rows =>
        {
            var cells = ((CsvVisibleRow)rows[0]!).Cells;
            Assert.Equal(CsvStructure.WidthForChars(40), cells[0].Width);
            Assert.Equal(CsvStructure.WidthForChars(8), cells[1].Width);
        });

    [Fact]
    public Task SetShape_ReChunksWithoutReWalking()
        => WithRows("[1,2,3,4,5,6]", Columns("value"), JsonArrayColumnMode.ByProperty, rows =>
        {
            Assert.Equal(6, rows.Count);

            rows.SetShape(Columns("Column 1", "Column 2", "Column 3"), JsonArrayColumnMode.Reshape);

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

            rows.SetShape(Columns("Column 1", "Column 2"), JsonArrayColumnMode.Reshape);

            Assert.NotNull(captured);
            Assert.Equal(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, captured!.Action);
            Assert.Equal(["1", "2"], TextOf(rows, 0));
        });

    [Fact]
    public Task SetShape_SameShape_DoesNotNotify()
        => WithRows("[1,2,3,4]", Columns("value"), JsonArrayColumnMode.ByProperty, rows =>
        {
            var structure = Columns("value");
            rows.SetShape(structure, JsonArrayColumnMode.ByProperty);

            bool raised = false;
            rows.CollectionChanged += (_, _) => raised = true;
            rows.SetShape(structure, JsonArrayColumnMode.ByProperty);

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
}
