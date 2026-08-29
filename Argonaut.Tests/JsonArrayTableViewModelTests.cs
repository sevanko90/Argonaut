using System.Text;
using Argonaut.Features.Json;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Verifies the array-table document: column discovery from the sampled elements, widths taken
/// from the child VALUE tokens rather than the element's own (which for an object is one byte -
/// the brace), the column-mode picker re-shaping without re-walking, and the status/failure
/// reporting the base class drives.
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

            Assert.Equal(60, idWidth); // "id" (2) and the value (1) both clamp to the minimum
            Assert.True(descriptionWidth > idWidth);
            return Task.CompletedTask;
        });

    [Fact]
    public Task ColumnWidthIsSeededByItsOwnHeaderSoTheLabelAlwaysFits()
        => WithDocument("""[{"a-long-column-header":1}]""", document =>
        {
            Assert.Equal(156, document.Structure.Columns[0].Width); // 20*7 + 16
            return Task.CompletedTask;
        });

    [Fact]
    public Task HeaderCellsMirrorTheStructure()
        => WithDocument("""[{"id":1,"name":"a"}]""", document =>
        {
            Assert.Equal(["id", "name"], document.HeaderCells.Select(c => c.Text));
            Assert.Equal(document.Structure.Columns[1].Width, document.HeaderCells[1].Width);
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
    public Task PickingByPropertyAgain_RestoresTheDiscoveredColumns()
        => WithDocument("""[{"id":1,"name":"a"},{"id":2,"name":"b"}]""", document =>
        {
            var toolbar = Assert.IsType<JsonArrayTableToolbarViewModel>(document.Toolbar);
            toolbar.SelectedColumnMode = toolbar.ColumnModes.Single(o => o.Columns == 2);
            Assert.Equal(["Column 1", "Column 2"], ColumnNames(document));

            toolbar.SelectedColumnMode = toolbar.ColumnModes.Single(o => o.Mode == JsonArrayColumnMode.ByProperty);

            Assert.Equal(["id", "name"], ColumnNames(document));
            Assert.Equal(2, document.RowCount);
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
}
