using System.Text;
using Argonaut.Features.Csv;
using Argonaut.Features.Json;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Verifies the array-table document: column discovery from the sampled elements, widths taken
/// from the text a cell will render - the child VALUE tokens rather than the element's own
/// (which for an object is one byte, the brace), and a container's summary rather than its
/// brace - the column-mode picker re-shaping without re-walking (and being offered at all only
/// for an array of scalars), and the status/failure reporting the base class drives.
/// </summary>
public class JsonArrayTableViewModelTests
{
    private static string WriteTempJson(string content)
    {
        string path = Path.GetTempFileName();
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        return path;
    }

    private static async Task WithDocument(string json, Func<JsonArrayTableViewModel, Task> assert)
    {
        string path = WriteTempJson(json);
        try
        {
            var document = new JsonArrayTableViewModel();
            try
            {
                await document.LoadAsync(path, 0, new FileInfo(path).Length, "$");
                await assert(document);
            }
            finally
            {
                document.Dispose();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string[] ColumnNames(JsonArrayTableViewModel document)
        => document.Structure.Columns.Select(c => c.Name).ToArray();

    [Fact]
    public Task DiscoversColumnsAsTheUnionOfPropertyNamesInFirstSeenOrder()
        => WithDocument("""[{"id":1,"name":"a"},{"name":"b","extra":true}]""", document =>
        {
            Assert.Equal(["id", "name", "extra"], ColumnNames(document));
            return Task.CompletedTask;
        });

    [Fact]
    public Task NonObjectElements_GetASingleValueColumn()
        => WithDocument("[1,2,3]", document =>
        {
            Assert.Equal(["value"], ColumnNames(document));
            Assert.Equal(3, document.RowCount);
            return Task.CompletedTask;
        });

    [Fact]
    public Task ColumnWidthsComeFromTheChildValueTokensNotTheElementBrace()
        => WithDocument("""[{"id":1,"description":"a-much-longer-value-here"}]""", document =>
        {
            // An element's own StartObject token has Length 1. Measuring that would collapse
            // every column to the minimum width, which is the bug this asserts against.
            double idWidth = document.Structure.Columns[0].Width;
            double descriptionWidth = document.Structure.Columns[1].Width;

            Assert.Equal(CsvStructure.MinColumnWidth, idWidth); // "id" (2) and the value (1) both clamp to the minimum
            Assert.True(descriptionWidth > idWidth);
            return Task.CompletedTask;
        });

    [Fact]
    public Task ColumnsOfNestedContainers_AreWidthedFromTheSummaryTheCellShows()
        => WithDocument("""[{"geometry":{"type":"Point","coordinates":[1,2]}}]""", document =>
        {
            // A StartObject token's own Length is 1, but the cell renders "{ 2 members }" - the
            // geojson case, where every container column came out at the minimum width and was
            // trimmed to "{ 2 me...". The width must fit the summary text instead.
            double width = document.Structure.Columns[0].Width;

            Assert.Equal(CsvStructure.WidthForChars("{ 2 members }".Length), width);
            Assert.True(width > CsvStructure.MinColumnWidth);
            return Task.CompletedTask;
        });

    [Fact]
    public Task ColumnWidthIsSeededByItsOwnHeaderSoTheLabelAlwaysFits()
        => WithDocument("""[{"a-long-column-header":1}]""", document =>
        {
            Assert.Equal(CsvStructure.WidthForChars("a-long-column-header".Length), document.Structure.Columns[0].Width);
            return Task.CompletedTask;
        });

    [Fact]
    public Task ColumnCountTracksTheStructure()
        => WithDocument("""[{"id":1,"name":"a"}]""", document =>
        {
            Assert.Equal(2, document.ColumnCount);
            Assert.Equal(document.Structure.ColumnCount, document.ColumnCount);
            return Task.CompletedTask;
        });

    [Fact]
    public Task PickingAReshapeWidth_ReChunksTheRows()
        => WithDocument("[1,2,3,4,5,6]", document =>
        {
            var toolbar = Assert.IsType<JsonArrayTableToolbarViewModel>(document.Toolbar);
            toolbar.SelectedColumnMode = toolbar.ColumnModes.Single(o => o.Mode == JsonArrayColumnMode.Reshape && o.Columns == 3);

            Assert.Equal(["Column 1", "Column 2", "Column 3"], ColumnNames(document));
            Assert.Equal(2, document.RowCount);
            return Task.CompletedTask;
        });

    [Fact]
    public Task PickingByPropertyAgain_RestoresTheValueColumn()
        => WithDocument("[1,2,3,4]", document =>
        {
            var toolbar = Assert.IsType<JsonArrayTableToolbarViewModel>(document.Toolbar);
            toolbar.SelectedColumnMode = toolbar.ColumnModes.Single(o => o.Columns == 2);
            Assert.Equal(["Column 1", "Column 2"], ColumnNames(document));

            toolbar.SelectedColumnMode = toolbar.ColumnModes.Single(o => o.Mode == JsonArrayColumnMode.ByProperty);

            Assert.Equal(["value"], ColumnNames(document));
            Assert.Equal(4, document.RowCount);
            return Task.CompletedTask;
        });

    [Fact]
    public Task AnArrayOfObjects_OffersNoReshapeAndHidesThePicker()
        => WithDocument("""[{"id":1,"name":"a"},{"id":2,"name":"b"}]""", document =>
        {
            var toolbar = Assert.IsType<JsonArrayTableToolbarViewModel>(document.Toolbar);

            // The property names ARE the columns, so there is nothing to re-width into.
            Assert.False(toolbar.CanReshape);
            Assert.Equal([JsonArrayColumnMode.ByProperty], toolbar.ColumnModes.Select(o => o.Mode));
            return Task.CompletedTask;
        });

    [Fact]
    public Task AnArrayOfScalars_OffersTheReshapeWidths()
        => WithDocument("[1,2,3]", document =>
        {
            var toolbar = Assert.IsType<JsonArrayTableToolbarViewModel>(document.Toolbar);

            Assert.True(toolbar.CanReshape);
            Assert.Contains(toolbar.ColumnModes, o => o.Mode == JsonArrayColumnMode.Reshape);
            return Task.CompletedTask;
        });

    [Fact]
    public Task AMixedArrayWithAnyObjectIn_KeepsTheByPropertyColumnsAndNoPicker()
        => WithDocument("""[1,{"id":2},3]""", document =>
        {
            var toolbar = Assert.IsType<JsonArrayTableToolbarViewModel>(document.Toolbar);

            Assert.False(toolbar.CanReshape);
            Assert.Equal(["id"], ColumnNames(document));
            return Task.CompletedTask;
        });

    [Fact]
    public async Task ToolbarBannerNamesWhereTheTableCameFrom()
    {
        string path = WriteTempJson("[1,2,3]");
        var document = new JsonArrayTableViewModel();
        try
        {
            await document.LoadAsync(path, 0, new FileInfo(path).Length, "$.items");

            var toolbar = Assert.IsType<JsonArrayTableToolbarViewModel>(document.Toolbar);
            Assert.Equal("$.items", toolbar.OriginPath);
            Assert.Equal(Path.GetFileName(path), toolbar.OriginFileName);
            Assert.Contains("$.items", toolbar.OriginDescription);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public Task BackInvokesTheInjectedNavigationWithTheOriginPath()
    {
        string path = WriteTempJson("[1,2,3]");
        return RunAsync();

        async Task RunAsync()
        {
            var document = new JsonArrayTableViewModel();
            try
            {
                string? navigatedTo = null;
                await document.LoadAsync(path, 0, new FileInfo(path).Length, "$.items",
                    navigateBack: p => { navigatedTo = p; return Task.CompletedTask; });

                var toolbar = Assert.IsType<JsonArrayTableToolbarViewModel>(document.Toolbar);
                await toolbar.BackAsync();

                Assert.Equal("$.items", navigatedTo);
            }
            finally
            {
                document.Dispose();
                File.Delete(path);
            }
        }
    }

    [Fact]
    public Task FindIsUnavailableWhileATableIsShowing()
        => WithDocument("[1,2,3]", document =>
        {
            Assert.Null(document.CreateSearchNavigator());
            return Task.CompletedTask;
        });

    [Fact]
    public Task ClaimsNoFileKindSoTheViewSwitcherOffersNoSelection()
        => WithDocument("[1,2,3]", document =>
        {
            foreach (FileTypeDetector.FileKind kind in Enum.GetValues<FileTypeDetector.FileKind>())
                Assert.False(document.CanHandleFileType(kind));

            return Task.CompletedTask;
        });

    [Fact]
    public Task ReportsItsRowTotalOnceIndexingCompletes()
        => WithDocument("[1,2,3]", async document =>
        {
            await document.IndexingTask;

            Assert.Contains("3 rows", document.StatusText);
            Assert.Null(document.IndexFailure);
        });

    [Fact]
    public async Task MalformedRange_SurfacesAsAnIndexFailure()
    {
        string path = WriteTempJson("[1,2,3]");
        try
        {
            var document = new JsonArrayTableViewModel();
            try
            {
                await document.LoadAsync(path, 0, 4, "$"); // "[1,2" - not a whole JSON value
                try
                {
                    await document.IndexingTask;
                }
                catch
                {
                    // The document reports the failure; the shell never awaits the result.
                }

                Assert.NotNull(document.IndexFailure);
            }
            finally
            {
                document.Dispose();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public Task SubRange_TablesOnlyTheArrayItWasPointedAt()
    {
        string json = """{"before":[9,9],"items":[{"id":1},{"id":2},{"id":3}]}""";
        string path = WriteTempJson(json);
        return RunAsync();

        async Task RunAsync()
        {
            int offset = json.IndexOf("[{", StringComparison.Ordinal);
            int length = json.LastIndexOf(']') + 1 - offset;

            var document = new JsonArrayTableViewModel();
            try
            {
                await document.LoadAsync(path, offset, length, "$.items");

                Assert.Equal(["id"], ColumnNames(document));
                Assert.Equal(3, document.RowCount);
            }
            finally
            {
                document.Dispose();
                File.Delete(path);
            }
        }
    }

    [Fact]
    public Task DisposeIsIdempotent()
        => WithDocument("[1,2,3]", document =>
        {
            document.Dispose();
            document.Dispose();
            return Task.CompletedTask;
        });

    [Fact]
    public Task Nesting_ReportsWhichColumnsHaveSomethingInsideThem()
        => WithDocument("""[{"id":1,"geometry":{"type":"Point"},"bbox":[1,2,3]}]""", document =>
        {
            Assert.Equal(["id", "geometry", "bbox"], ColumnNames(document));

            Assert.False(document.Nesting[0].CanExpand);
            Assert.True(document.Nesting[1].HasObjects);
            Assert.False(document.Nesting[1].HasArrays);
            Assert.True(document.Nesting[2].HasArrays);
            return Task.CompletedTask;
        });

    [Fact]
    public Task Nesting_ReportsTheWidestArraySeenInAColumn()
        => WithDocument("""[{"bbox":[1,2]},{"bbox":[1,2,3,4]},{"bbox":[1,2,3]}]""", document =>
        {
            // How many index columns expanding this one would draw, before the cap applies.
            Assert.Equal(4, document.Nesting[0].WidestArity);
            return Task.CompletedTask;
        });

    [Fact]
    public Task Nesting_StopsCountingAtTheArityCap()
        => WithDocument($$"""[{"wide":[{{string.Join(',', Enumerable.Range(0, 200))}}]}]""", document =>
        {
            // Past the cap the number is no longer an expansion width, so the walk stops rather
            // than counting a 7,982-element array out to the end.
            Assert.Equal(ColumnNesting.ArityCap, document.Nesting[0].WidestArity);
            return Task.CompletedTask;
        });

    [Fact]
    public Task Nesting_MeasuresAContainerFromItsBraceToItsCloseNotItsOwnToken()
        => WithDocument("""[{"geometry":{"type":"Point","coordinates":[1,2]}}]""", document =>
        {
            // A StartObject token's own Length is 1; the column's size is the whole span.
            Assert.Equal("""{"type":"Point","coordinates":[1,2]}""".Length, document.Nesting[0].LargestBytes);
            return Task.CompletedTask;
        });

    [Fact]
    public Task Routes_AreFlatUntilSomethingIsExpanded()
        => WithDocument("""[{"id":1,"geometry":{"type":"Point"}}]""", document =>
        {
            Assert.True(document.Routes.TryMatchName("geometry"u8, out int column, out var inner));
            Assert.Equal(1, column);
            Assert.Null(inner); // discovery expands nothing - a container is a summary cell
            return Task.CompletedTask;
        });

    [Fact]
    public Task Routes_AreEmptyForAnArrayOfScalars()
        => WithDocument("[1,2,3]", document =>
        {
            // The single "value" column is the element itself, not a property of it.
            Assert.True(document.Routes.IsEmpty);
            Assert.Single(document.Nesting);
            return Task.CompletedTask;
        });

    [Fact]
    public Task Reshape_ClearsTheRoutesAndTheNesting()
        => WithDocument("[1,2,3,4]", document =>
        {
            var toolbar = Assert.IsType<JsonArrayTableToolbarViewModel>(document.Toolbar);
            toolbar.SelectedColumnMode = toolbar.ColumnModes.Single(o => o.Mode == JsonArrayColumnMode.Reshape && o.Columns == 2);

            Assert.True(document.Routes.IsEmpty);
            Assert.Empty(document.Nesting);
            return Task.CompletedTask;
        });

    private static string[] CellsOf(JsonArrayTableViewModel document, int row)
        => ((CsvVisibleRow)document.Rows[row]!).Cells.Select(c => c.Text).ToArray();

    /// <summary>The key a header piece toggles - what a click on it hands the document.</summary>
    private static string KeyOf(JsonArrayTableViewModel document, int column)
        => document.Headers[column].Segments[^1].Key
           ?? throw new InvalidOperationException($"Column {column} offers nothing to expand.");

    [Fact]
    public Task ExpandingAnObjectColumn_ReplacesItWithItsChildren()
        => WithDocument("""[{"id":1,"geometry":{"type":"Point","nested":{"a":1}}}]""", document =>
        {
            document.ToggleColumn(KeyOf(document, 1));

            Assert.Equal(["id", "geometry.type", "geometry.nested"], ColumnNames(document));
            // The child that was not expanded is still a summary - depth grows by clicks only.
            Assert.Equal(["1", "\"Point\"", "{ 1 member }"], CellsOf(document, 0));
            return Task.CompletedTask;
        });

    [Fact]
    public Task ExpandingTwice_CollapsesBackToTheContainerColumn()
        => WithDocument("""[{"geometry":{"type":"Point"}}]""", document =>
        {
            string key = KeyOf(document, 0);
            document.ToggleColumn(key);
            Assert.Equal(["geometry.type"], ColumnNames(document));

            // The ancestor piece of the expanded column's header toggles the same key.
            Assert.Equal(key, document.Headers[0].Segments[0].Key);
            document.ToggleColumn(key);

            Assert.Equal(["geometry"], ColumnNames(document));
            Assert.Equal(["{ 1 member }"], CellsOf(document, 0));
            return Task.CompletedTask;
        });

    [Fact]
    public Task CollapsingAContainer_ClosesWhatWasOpenInsideIt()
        => WithDocument("""[{"a":{"b":{"c":1}}}]""", document =>
        {
            string outer = KeyOf(document, 0);
            document.ToggleColumn(outer);
            document.ToggleColumn(KeyOf(document, 0)); // a.b
            Assert.Equal(["a.b.c"], ColumnNames(document));

            document.ToggleColumn(outer);
            document.ToggleColumn(outer);

            // Reopening shows a's own children, not the expansion from before it was closed.
            Assert.Equal(["a.b"], ColumnNames(document));
            return Task.CompletedTask;
        });

    [Fact]
    public Task ExpandingAnArray_DrawsTheFirstPositionsAndSummarisesTheRest()
        => WithDocument("""[{"coordinates":[10,20,30,40,50,60,70]}]""", document =>
        {
            document.ToggleColumn(KeyOf(document, 0));

            // Four positions by default, then one remainder column carrying the real count -
            // this is the $.features[7].geometry.coordinates[7982] case in miniature.
            Assert.Equal(
                ["coordinates[0]", "coordinates[1]", "coordinates[2]", "coordinates[3]", "coordinates[…]"],
                ColumnNames(document));
            Assert.Equal(["10", "20", "30", "40", "[ 7 items ]"], CellsOf(document, 0));
            return Task.CompletedTask;
        });

    [Fact]
    public Task ExpandingAnArrayThatFits_DrawsNoRemainderColumn()
        => WithDocument("""[{"bbox":[1,2,3]}]""", document =>
        {
            document.ToggleColumn(KeyOf(document, 0));

            Assert.Equal(["bbox[0]", "bbox[1]", "bbox[2]"], ColumnNames(document));
            return Task.CompletedTask;
        });

    [Fact]
    public Task ArrayColumns_ChangesHowManyPositionsAnOpenArrayDraws()
        => WithDocument("""[{"bbox":[1,2,3,4,5,6]}]""", document =>
        {
            document.ToggleColumn(KeyOf(document, 0));
            Assert.Equal(5, document.ColumnCount); // four positions plus the remainder

            document.ArrayColumns = 2;

            Assert.Equal(["bbox[0]", "bbox[1]", "bbox[…]"], ColumnNames(document));
            return Task.CompletedTask;
        });

    [Fact]
    public Task ArrayColumns_IsOfferedOnlyWhenAColumnHoldsAnArray()
        => WithDocument("""[{"id":1,"geometry":{"bbox":[1,2]}}]""", document =>
        {
            var toolbar = Assert.IsType<JsonArrayTableToolbarViewModel>(document.Toolbar);
            Assert.False(toolbar.CanExpandArrays);

            // Expanding an object can reveal an array nobody could see when the table opened.
            document.ToggleColumn(KeyOf(document, 1));

            Assert.True(toolbar.CanExpandArrays);
            return Task.CompletedTask;
        });

    [Fact]
    public Task HeaderSegments_LinkTheContainersAndLeaveScalarsPlain()
        => WithDocument("""[{"geometry":{"type":"Point"}}]""", document =>
        {
            document.ToggleColumn(KeyOf(document, 0));

            var segments = document.Headers[0].Segments;
            Assert.Equal(["geometry", ".type"], segments.Select(s => s.Text));
            Assert.NotNull(segments[0].Key);  // collapses back to geometry
            Assert.Null(segments[1].Key);     // a scalar has nothing to open
            return Task.CompletedTask;
        });

    [Fact]
    public async Task TooManyColumns_StopsAtTheCapAndSaysSo()
    {
        string wide = "[{" + string.Join(',', Enumerable.Range(0, 200).Select(i => $"\"p{i}\":{i}")) + "}]";

        string? toast = null;
        void Capture(string message) => toast = message;
        ToastService.Requested += Capture;
        try
        {
            await WithDocument(wide, document =>
            {
                Assert.Equal(JsonArrayColumnDiscovery.MaxColumns, document.ColumnCount);
                return Task.CompletedTask;
            });
        }
        finally
        {
            ToastService.Requested -= Capture;
        }

        Assert.Contains($"{JsonArrayColumnDiscovery.MaxColumns} columns", toast);
    }

    [Fact]
    public Task ReshapeHeaders_OfferNothingToExpand()
        => WithDocument("[1,2,3,4]", document =>
        {
            var toolbar = Assert.IsType<JsonArrayTableToolbarViewModel>(document.Toolbar);
            toolbar.SelectedColumnMode = toolbar.ColumnModes.Single(o => o.Mode == JsonArrayColumnMode.Reshape && o.Columns == 2);

            Assert.Equal(2, document.Headers.Count);
            Assert.All(document.Headers, header => Assert.Null(header.Segments[^1].Key));
            return Task.CompletedTask;
        });

    [Fact]
    public Task ExpandedChildrenStayTogether_EvenWhenALaterElementIntroducesOne()
        => WithDocument("""
            [{"type":"Feature","geometry":{"type":"Point"},"properties":{"a":1}},
             {"type":"Feature","geometry":{"type":"Collection","geometries":[]},"properties":{"a":2}}]
            """, document =>
        {
            document.ToggleColumn(KeyOf(document, 1));

            // geometry.geometries is first seen in element 2, long after properties was
            // registered from element 1 - it still belongs beside its siblings.
            Assert.Equal(
                ["type", "geometry.type", "geometry.geometries", "properties"],
                ColumnNames(document));
            return Task.CompletedTask;
        });

    [Fact]
    public Task ExpandedChildrenStayTogether_WithTheirCellsStillInTheRightColumns()
        => WithDocument("""
            [{"a":{"x":1},"z":9},
             {"a":{"x":2,"y":3},"z":8}]
            """, document =>
        {
            document.ToggleColumn(KeyOf(document, 0));

            Assert.Equal(["a.x", "a.y", "z"], ColumnNames(document));
            // The re-order has to carry the routes with it, or the cells land in the columns the
            // registration order would have put them in.
            Assert.Equal(["1", "", "9"], CellsOf(document, 0));
            Assert.Equal(["2", "3", "8"], CellsOf(document, 1));
            return Task.CompletedTask;
        });

    [Fact]
    public Task ExpandedArrayRemainder_StaysWithItsPositions()
        => WithDocument("""[{"bbox":[1,2,3,4,5,6],"z":9}]""", document =>
        {
            document.ToggleColumn(KeyOf(document, 0));

            Assert.Equal(["bbox[0]", "bbox[1]", "bbox[2]", "bbox[3]", "bbox[…]", "z"], ColumnNames(document));
            return Task.CompletedTask;
        });
}
