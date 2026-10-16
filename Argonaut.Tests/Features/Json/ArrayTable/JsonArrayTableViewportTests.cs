using System.Text.Json;
using Argonaut.Features.Json;
using Argonaut.Features.Json.ArrayTable;
using Argonaut.Tests.Support;
using Argonaut.Ui.TableGrid;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Argonaut.Tests.Features.Json.ArrayTable;

/// <summary>
/// Headless tests for <see cref="JsonArrayTableView"/> on its surface: a click on a cell far to
/// the right opens that cell's value, a click on a header's link opens its container into
/// columns, and the cells say under the pointer that they open something.
/// </summary>
public sealed class JsonArrayTableViewportTests
{
    /// <summary>96 plain properties, then one object - a grid far wider than the window, with
    /// one column to expand at its far end.</summary>
    private static string WideArray()
    {
        var properties = Enumerable.Range(0, 96)
            .ToDictionary(c => $"property{c}", c => (object)$"content of property {c}");
        properties["property96"] = new { child = "child of last property", second = "second child" };
        return JsonSerializer.Serialize(Enumerable.Repeat(properties, 30));
    }

    private static Task Run(string json, Func<JsonArrayTableViewModel, TableSurface, Window, Task> test)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(JsonArrayTableViewportTests).Assembly);
        return session.Dispatch(async () =>
        {
            string path = Path.GetTempFileName();
            File.WriteAllText(path, json);
            using var document = new JsonArrayTableViewModel(new JsonViewSettings());
            Window? window = null;
            try
            {
                await document.LoadAsync(path, 0, new FileInfo(path).Length, "$");
                var view = new JsonArrayTableView { DataContext = document };
                window = new Window { Width = 1_400, Height = 500, Content = view };
                window.Show();
                await SettleAsync(window);
                var surface = view.GetVisualDescendants().OfType<TableSurface>().Single();
                await test(document, surface, window);
                return true;
            }
            finally
            {
                window?.Close();
                document.Dispose();
                File.Delete(path);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public Task Cells_and_header_links_far_to_the_right_open_what_they_name() => Run(WideArray(), async (document, surface, window) =>
    {
        surface.RevealColumn(95);
        await SettleAsync(window);
        Click(window, CellCentre(surface, row: 0, column: 95));
        await SettleAsync(window);
        Assert.Equal("content of property 95", document.CellDetail?.Text);
        Assert.Equal("property95 — row 1", document.CellDetail?.Title);
        Assert.Equal(0, surface.SelectedIndex);

        document.CloseCellDetail();
        surface.RevealColumn(96);
        await SettleAsync(window);
        Click(window, HeaderTextStart(surface, column: 96));
        await SettleAsync(window);
        Assert.Equal(98, document.ColumnCount);

        surface.RevealColumn(96);
        await SettleAsync(window);
        Click(window, CellCentre(surface, row: 2, column: 96));
        await SettleAsync(window);
        Assert.Equal("child of last property", document.CellDetail?.Text);
        Assert.Contains("property96", document.CellDetail!.Title);
        Assert.Contains("child", document.CellDetail.Title);
        Assert.Contains("row 3", document.CellDetail.Title);
    });

    [Fact]
    public Task An_expansion_at_the_same_count_starts_the_columns_widths_again() => Run(
        $$"""[{"{{LongName}}":{"x":1},"b":1}]""", async (document, surface, window) =>
        {
            var before = surface.Columns!;
            double edge = before.Right(1);
            window.MouseDown(new Point(edge, 10), MouseButton.Left);
            window.MouseMove(new Point(edge + 50, 10));
            window.MouseUp(new Point(edge + 50, 10), MouseButton.Left);
            await SettleAsync(window);
            Assert.True(surface.Columns!.IsUserSized(1));

            // The object opens into its one child: still two columns, opening at the same widths -
            // a name past the widest a column opens either way - but different columns.
            Click(window, HeaderTextStart(surface, column: 0));
            await SettleAsync(window);

            Assert.Equal(2, document.ColumnCount);
            Assert.False(surface.Columns!.IsUserSized(1));
        });

    [Fact]
    public Task A_cell_under_the_pointer_says_a_click_opens_it() => Run(WideArray(), async (document, surface, window) =>
    {
        window.MouseMove(CellCentre(surface, row: 0, column: 1));
        await SettleAsync(window);

        var tip = Assert.IsType<StackPanel>(surface.CurrentTip);
        var texts = tip.Children.OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToArray();
        Assert.Equal(["\"content of property 1\"", "Click to open this cell in the detail pane"], texts);

        window.MouseMove(HeaderTextStart(surface, column: 1));
        await SettleAsync(window);
        Assert.Equal("property1", surface.CurrentTip);
    });

    private static readonly string LongName = new('a', 50);

    private static Point CellCentre(TableSurface surface, int row, int column)
    {
        var columns = surface.Columns!;
        double x = surface.GutterWidth + columns.Left(column) - surface.PanOffset + columns.Width(column) / 2;
        double y = TableSurface.HeaderHeight + (row + 0.5) * TableSurface.RowHeight;
        return new Point(x, y);
    }

    /// <summary>Just inside a header's text, where its first piece is drawn.</summary>
    private static Point HeaderTextStart(TableSurface surface, int column)
    {
        double x = surface.GutterWidth + surface.Columns!.Left(column) - surface.PanOffset + TableSurface.CellPaddingX + 4;
        return new Point(x, TableSurface.HeaderHeight / 2);
    }

    private static async Task SettleAsync(Window window)
    {
        await Task.Delay(30);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void Click(Window window, Point point)
    {
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }
}
