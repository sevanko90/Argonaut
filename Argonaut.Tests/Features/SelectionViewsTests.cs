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
    private static SelectionByteOrigin Select(string file, string filter, SelectionUnit unit = SelectionUnit.Lines)
    {
        var whole = new MemoryByteOrigin(Encoding.UTF8.GetBytes(file), "big.txt");
        var selection = LineSelectionLocator.Locate(whole.Open(), SelectionFilter.Parse(new StringReader(filter), unit));
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

            Assert.Equal(["line 3", "line 10", "line 11", "line 50", ""],
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

    /// <summary>
    /// What the shell does when a followed filter file changes, from the CSV side: where the user
    /// was, mapped to the file and into the new selection. A selected row on screen is selected
    /// again; otherwise the top row on screen goes back to the top, with nothing selected - and so
    /// does the next row still listed, when the filter dropped the one that was selected.
    /// </summary>
    /// <param name="selected">The data row selected before, or -1 for none.</param>
    /// <param name="selectedAfter">The first cell of the row selected after, or null for none.</param>
    /// <param name="topAfter">The first cell of the row put at the top after, or null for none.</param>
    [Theory]
    [InlineData(1, "2, 4, 8-9", "7", null)]     // selected and still listed: selected again
    [InlineData(1, "2, 4, 9, 15", null, "8")]   // selected but dropped: the next listed row, at the top
    [InlineData(-1, "2, 4, 8-9", null, "7")]    // nothing selected: the top row stays at the top
    [InlineData(0, "2, 4, 8-9", null, "7")]     // selected but scrolled away from: the top row, as raw does
    public async Task Csv_comes_back_to_where_the_user_was_when_the_filter_changes(int selected, string changedFilter, string? selectedAfter, string? topAfter)
    {
        string file = "id,name\n" + Lines(20, n => $"{n},row {n}");
        var before = new CsvViewModel();
        var after = new CsvViewModel();
        try
        {
            var shown = Select(file, "4, 8-9, 15"); // rows "3", "7", "8", "14"
            await before.LoadAsync(shown, (byte)',');
            await before.IndexingTask;
            before.RecordRowsOnScreen(1, 2);
            before.RecordSelectedRow(selected >= 0 ? selected : null);

            var place = shown.FileRangeOf(before.SelectedByteRange!.Value);
            var next = Select(file, changedFilter);
            await after.LoadAsync(next, (byte)',');
            await after.RevealByteRangeAsync(next.ViewRangeOf(place));

            Assert.Equal(selectedAfter, after.SelectedRowIndex is int row ? FirstCell(after, row) : null);
            Assert.Equal(topAfter, after.RowShownAtTop is int top ? FirstCell(after, top) : null);
            Assert.Null(after.SelectedColumnIndex);
        }
        finally
        {
            before.Dispose();
            after.Dispose();
        }
    }

    private static string FirstCell(CsvViewModel vm, int row) => ((Argonaut.Ui.TableGrid.TableRow)vm.Rows[row]!).Cells[0].Text;

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

    // A run with no newline of its own - a byte range ending mid-row, or the file's last line -
    // is followed by one the view adds, which is not beside the row's bytes. Reading a row must
    // not ask for the two together.

    [Fact]
    public async Task Csv_reads_rows_a_byte_range_cuts_off_mid_row()
    {
        const string file = "id,name\n1,one\n2,two\n3,three\n";
        int cut = file.IndexOf("2,tw", StringComparison.Ordinal) + 4;
        var vm = new CsvViewModel();
        try
        {
            await vm.LoadAsync(Select(file, $"8-{cut - 1}", SelectionUnit.Bytes), (byte)',');
            await vm.IndexingTask;

            Assert.Equal([["1", "one"], ["2", "tw"]],
                Enumerable.Range(0, vm.RowCount).Select(i => ((Argonaut.Ui.TableGrid.TableRow)vm.Rows[i]!).Cells.Select(c => c.Text).ToArray()));
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task NdJson_reads_and_parses_a_last_line_with_no_newline()
    {
        var vm = new NdJsonViewModel(new JsonViewSettings(), new SchemaBindings(), TestSchemas.Catalog());
        try
        {
            await vm.LoadAsync(Select("{\"n\":1}\n{\"n\":2}\n{\"n\":3}", "1, 3"));
            await vm.IndexingTask;

            Assert.Equal("{\"n\":3}", ((NdJsonVisibleLine)vm.Lines[1]!).Text);

            vm.LoadSelectedLine(1);
            for (int i = 0; i < 200 && vm.SelectedLineJsonViewModel is null; i++)
                await Task.Delay(10);
            Assert.NotNull(vm.SelectedLineJsonViewModel);
        }
        finally
        {
            vm.Dispose();
        }
    }
}
