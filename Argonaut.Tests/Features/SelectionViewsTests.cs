using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Selection;
using Argonaut.Features.Csv;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Schema;
using Argonaut.Features.NdJson;
using Argonaut.Features.Raw;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Features;

/// <summary>
/// The three views a selection opens in - Raw, CSV and NDJSON - over a real selection: each shows
/// the selected lines and nothing between them, reading every line and field through the seams
/// without asking for a range that crosses one. And each opens an empty selection - what a
/// followed filter that lists nothing for now produces - as an empty document.
/// </summary>
public class SelectionViewsTests
{
    private static SelectionByteOrigin Select(string file, string filter)
    {
        var whole = new MemoryByteOrigin(Encoding.UTF8.GetBytes(file), "big.txt");
        var selection = LineSelectionLocator.Locate(whole.Open(), SelectionFilter.Parse(new StringReader(filter), SelectionUnit.Lines));
        return new SelectionByteOrigin(whole, selection);
    }

    private static string Lines(int count, Func<int, string> line)
        => string.Concat(Enumerable.Range(1, count).Select(n => line(n) + "\n"));

    [Fact]
    public async Task Raw_shows_the_selected_lines()
    {
        var vm = new RawViewModel(new RawViewSettings());
        try
        {
            await vm.LoadAsync(Select(Lines(50, n => $"line {n}"), "3, 10-11, 50"));
            await vm.IndexingTask;

            Assert.Equal(["line 3", "line 10", "line 11", "line 50"],
                Enumerable.Range(0, vm.RowCount).Select(i => ((RawVisibleRow)vm.Rows[i]!).Text));
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task Csv_shows_the_selected_rows_under_the_files_header()
    {
        var vm = new CsvViewModel();
        try
        {
            await vm.LoadAsync(Select("id,name\n" + Lines(20, n => $"{n},row {n}"), "4, 8-9"), (byte)',');
            await vm.IndexingTask;

            Assert.Equal(["id", "name"], vm.Structure.Columns.Select(c => c.Name));
            Assert.Equal(["3", "7", "8"], Enumerable.Range(0, vm.RowCount).Select(i => ((Argonaut.Ui.TableGrid.TableRow)vm.Rows[i]!).Cells[0].Text));
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task NdJson_shows_and_parses_the_selected_lines()
    {
        var vm = new NdJsonViewModel(new JsonViewSettings(), new SchemaBindings(), TestSchemas.Catalog());
        try
        {
            await vm.LoadAsync(Select(Lines(30, n => $"{{\"n\":{n}}}"), "2, 20-21"));
            await vm.IndexingTask;

            Assert.Equal(3, vm.LineCount);
            Assert.Equal("{\"n\":20}", ((NdJsonVisibleLine)vm.Lines[1]!).Text);

            vm.LoadSelectedLine(2);
            for (int i = 0; i < 200 && vm.SelectedLineJsonViewModel is null; i++)
                await Task.Delay(10);
            Assert.NotNull(vm.SelectedLineJsonViewModel);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task Each_view_opens_an_empty_selection_as_an_empty_document()
    {
        const string file = "a,b\n1,2\n";

        var raw = new RawViewModel(new RawViewSettings());
        var csv = new CsvViewModel();
        var ndjson = new NdJsonViewModel(new JsonViewSettings(), new SchemaBindings(), TestSchemas.Catalog());
        try
        {
            await raw.LoadAsync(Select(file, "9-"));
            await raw.IndexingTask;
            await csv.LoadAsync(Select(file, "9-"), (byte)',');
            await csv.IndexingTask;
            await ndjson.LoadAsync(Select(file, "9-"));
            await ndjson.IndexingTask;

            Assert.Equal(0, csv.RowCount);
            Assert.Equal(0, ndjson.LineCount);
            Assert.Null(raw.IndexFailure);
        }
        finally
        {
            raw.Dispose();
            csv.Dispose();
            ndjson.Dispose();
        }
    }
}
