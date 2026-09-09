using System.Text.Json;
using Argonaut.Features.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Argonaut.Tests;

public sealed class JsonArrayTableViewportTests
{
    [Fact]
    public Task HorizontalScroll_CellDetailsAndHeaderExpansionUseTheLogicalProperty()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(JsonArrayTableViewportTests).Assembly);
        return session.Dispatch(async () =>
        {
            var properties = Enumerable.Range(0, 96)
                .ToDictionary(c => $"property{c}", c => (object)$"content of property {c}");
            properties["property96"] = new { child = "child of last property", second = "second child" };
            string path = Path.GetTempFileName();
            File.WriteAllText(path, JsonSerializer.Serialize(Enumerable.Repeat(properties, 30)));
            using var document = new JsonArrayTableViewModel();
            Window? window = null;
            try
            {
                await document.LoadAsync(path, 0, new FileInfo(path).Length, "$");
                var view = new JsonArrayTableView { DataContext = document };
                window = new Window { Width = 1_400, Height = 500, Content = view };
                window.Show();
                await SettleAsync(window);
                var table = view.GetVisualDescendants().OfType<TableView>().Single();
                var scroll = Assert.IsType<ScrollViewer>(table.Scroll);
                scroll.Offset = new Vector(scroll.Extent.Width, 0);
                await SettleAsync(window);

                var text = table.GetVisualDescendants().OfType<TextBlock>()
                    .First(t => t.Text == "\"content of property 95\"");
                Click(window, text);
                await SettleAsync(window);
                Assert.Equal("content of property 95", document.CellDetail?.Text);
                Assert.Equal("property95 — row 1", document.CellDetail?.Title);

                document.CloseCellDetail();
                await SettleAsync(window);
                scroll.Offset = new Vector(scroll.Extent.Width, 0);
                await SettleAsync(window);
                var link = table.GetVisualDescendants().OfType<Button>()
                    .Single(b => b.Content is TextBlock { Text: "property96" });
                Click(window, link);
                await SettleAsync(window);
                Assert.Equal(98, document.ColumnCount);
                scroll.Offset = new Vector(scroll.Extent.Width, 0);
                await SettleAsync(window);

                var child = table.GetVisualDescendants().OfType<TextBlock>()
                    .First(t => t.Text == "\"child of last property\"");
                Click(window, child);
                await SettleAsync(window);
                Assert.Equal("child of last property", document.CellDetail?.Text);
                Assert.Contains("property96", document.CellDetail!.Title);
                Assert.Contains("child", document.CellDetail.Title);
                Assert.InRange(table.Columns.Count, 1, 20);

                // Closing with a pending viewport change must not resurrect the columns.
                scroll.Offset = new Vector(0, 0);
                window.Close();
                await SettleAsync(window);
                Assert.Empty(table.Columns);
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

    private static async Task SettleAsync(Window window)
    {
        await Task.Delay(30);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void Click(Window window, Control target)
    {
        var centre = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("Target is outside the window.");
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
    }
}
