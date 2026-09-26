using System.Text;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Schema;
using Argonaut.Tests.Support;
using Argonaut.Ui.Tree;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Argonaut.Tests.Features.Json;

/// <summary>
/// The JSON view end to end in a headless window: a loaded document draws rows, a reveal (what a
/// search hit or a path jump does) selects the row holding the offset and fills in the path bar,
/// and a clicked path segment goes back up. Harness rule (docs/headless-test-dispatch-hole.md):
/// the dispatch body returns true and the test returns the dispatch task.
/// </summary>
public sealed class JsonViewTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"view-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(path);

    private static async Task PumpAsync()
    {
        await Task.Delay(30);
        Dispatcher.UIThread.RunJobs();
    }

    private Task WithView(string json, Func<Window, JsonViewModel, TreeSurface, Task> body)
    {
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(json));
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(JsonViewTests).Assembly);
        return session.Dispatch(async () =>
        {
            var vm = new JsonViewModel(new JsonViewSettings(), new SchemaBindings(), TestSchemas.Catalog());
            var window = new Window { Width = 900, Height = 500 };
            try
            {
                await vm.LoadAsync(path);
                await vm.IndexingTask;
                window.Content = new JsonView { DataContext = vm };
                window.Show();
                await PumpAsync();
                window.UpdateLayout();
                await PumpAsync();

                var surface = window.GetVisualDescendants().OfType<TreeSurface>().Single();
                await body(window, vm, surface);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    private static string BigDocument()
    {
        var json = new StringBuilder("{\"meta\":{\"name\":\"test\"},\"items\":[");
        for (int i = 0; i < 20_000; i++)
            json.Append(i == 0 ? "" : ",").Append($"{{\"id\":{i},\"tags\":[\"a\",\"b\"],\"child\":{{\"deep\":\"v{i}\"}}}}");
        return json.Append("]}").ToString();
    }

    [Fact]
    public Task ALoadedDocumentDrawsItsFirstRows() => WithView(BigDocument(), (_, vm, surface) =>
    {
        Assert.Same(vm.Tree, surface.Document);
        Assert.InRange(surface.RealizedRows.Count, 10, 40);
        Assert.Equal(0, surface.RealizedRows[0].Depth);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ARevealSelectsTheRowAndFillsInThePath() => WithView(BigDocument(), async (_, vm, surface) =>
    {
        string json = File.ReadAllText(path);
        long target = json.IndexOf("\"v15000\"", StringComparison.Ordinal);

        vm.Reveal(target);
        await PumpAsync();

        Assert.Equal(target, surface.SelectedRow!.Value.Node.ValueStart);
        Assert.Equal("$.items[15000].child.deep", vm.SelectedPath);
        Assert.Equal("v15000", vm.SelectedValueText);
        Assert.Contains(surface.SelectedRow.Value.Key, surface.RealizedRows.Select(r => r.Key));

        // Up the path bar: the "items" segment selects the array's own row.
        var items = vm.SelectedPathSegments[1];
        vm.Reveal(items.Target);
        await PumpAsync();
        Assert.Equal("$.items", vm.SelectedPath);
        Assert.Equal(TreeRowShape.Open, surface.SelectedRow!.Value.Shape);
    });

    [Fact]
    public Task NavigatingToAPathRevealsIt() => WithView(BigDocument(), async (_, vm, surface) =>
    {
        await vm.NavigateToPathAsync("$.items[19999].tags[1]");
        await PumpAsync();

        Assert.Equal("$.items[19999].tags[1]", vm.SelectedPath);
        Assert.Equal("b", vm.SelectedValueText);
    });

    [Fact]
    public Task BindingASchemaOpensTheGutter() => WithView("""{"id":1,"name":"x"}""", async (_, vm, surface) =>
    {
        double before = surface.ContentViewportWidth;
        var schema = JsonSchemaLoader.TryParse("""{"type":"object","properties":{"id":{"title":"Identifier"}}}""");
        vm.SchemaSettings.SetDocument(schema);
        await PumpAsync();

        Assert.True(surface.ContentViewportWidth < before - 100, "the schema gutter should take room from the rows");
    });
}
