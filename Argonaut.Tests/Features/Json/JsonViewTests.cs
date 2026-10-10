using System.Text;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Schema;
using Argonaut.Tests.Support;
using Argonaut.Ui.Rows;
using Argonaut.Ui.Tree;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
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
    public Task ARevealedMatchIsTheCurrentOneInItsRow() => WithView("""{"fruit":"banana bandana","other":"nab"}""", async (_, vm, surface) =>
    {
        string json = File.ReadAllText(path);
        vm.HighlightTerm = "an";
        long third = json.IndexOf("an", json.IndexOf("ban", json.IndexOf("banana", StringComparison.Ordinal) + 1, StringComparison.Ordinal), StringComparison.Ordinal);

        vm.RevealMatch(third);
        await PumpAsync();

        Assert.Equal(new TreeCurrentMatch(surface.SelectedRow!.Value.Key, 2), surface.CurrentMatch);

        // Anything else revealed is not a match, and nothing is current.
        vm.Reveal(json.IndexOf("\"nab\"", StringComparison.Ordinal));
        await PumpAsync();
        Assert.Null(surface.CurrentMatch);
    });

    private static string Jwt()
    {
        string Part(string json) => System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
        return $"{Part("""{"alg":"HS256"}""")}.{Part("""{"sub":"u1","exp":2000000000}""")}.c2ln";
    }

    /// <summary>A JWT's chip opens a card over the tree with its header and claims as a tree of
    /// their own; a click elsewhere closes it and lets the preview go.</summary>
    [Fact]
    public Task ClickingAJwtChipOpensACardWithItsClaims() => WithView($$"""{"token":"{{Jwt()}}"}""", async (window, vm, surface) =>
    {
        int index = surface.RealizedRows.ToList().FindIndex(r => r.Shape == TreeRowShape.Leaf);
        var chip = surface.LinkBounds(index)!.Value;
        var point = surface.TranslatePoint(chip.Center, window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        for (int i = 0; i < 50 && !CardIsOpen(window); i++)
            await PumpAsync();

        var view = window.GetVisualDescendants().OfType<JsonView>().Single();
        var card = view.FindControl<Avalonia.Controls.Primitives.Popup>("HintCard")!;
        Assert.True(card.IsOpen);
        var preview = Assert.IsType<Argonaut.Features.Json.Preview.ValuePreview>(view.FindControl<Argonaut.Features.Json.Preview.ValuePreviewView>("HintCardPreview")!.DataContext);
        Assert.True(preview.IsTree);
        Assert.Equal("JWT · header and claims", preview.Title);

        card.IsOpen = false;
        await PumpAsync();
        Assert.Null(view.FindControl<Argonaut.Features.Json.Preview.ValuePreviewView>("HintCardPreview")!.DataContext);
    });

    private static bool CardIsOpen(Window window)
        => window.GetVisualDescendants().OfType<JsonView>().Single().FindControl<Avalonia.Controls.Primitives.Popup>("HintCard")!.IsOpen;

    [Fact]
    public Task AHintPreviewsWhatItsValueEncodes() => WithView("""{"embedded":"{\"a\":1,\"b\":[2]}","text":"aGVsbG8gdGhlcmUsIHJlYWRlcg==","bad":"{\"a\":"}""", async (_, vm, _) =>
    {
        string json = File.ReadAllText(path);
        Argonaut.Features.Json.Tree.ExpandHintLink Link(string key, Argonaut.Features.Json.Hints.ValueHintKind kind)
        {
            int start = json.IndexOf('"', json.IndexOf($"\"{key}\":", StringComparison.Ordinal) + key.Length + 3);
            int end = json.IndexOf('"', start + 1);
            while (json[end - 1] == '\\')
                end = json.IndexOf('"', end + 1);
            return new(start, end + 1, kind);
        }

        var embedded = await vm.PreviewHintAsync(Link("embedded", Argonaut.Features.Json.Hints.ValueHintKind.EmbeddedJson));
        Assert.True(embedded!.Preview.IsTree);

        var text = await vm.PreviewHintAsync(Link("text", Argonaut.Features.Json.Hints.ValueHintKind.Base64));
        Assert.Equal("hello there, reader", text!.Preview.Text);

        Assert.Null(await vm.PreviewHintAsync(Link("bad", Argonaut.Features.Json.Hints.ValueHintKind.EmbeddedJson)));
        embedded.Preview.Dispose();
        text.Preview.Dispose();
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
        double before = surface.PanViewportWidth;
        var schema = JsonSchemaLoader.TryParse("""{"type":"object","properties":{"id":{"title":"Identifier"}}}""");
        vm.SchemaSettings.SetDocument(schema);
        await PumpAsync();

        Assert.True(surface.PanViewportWidth < before - 100, "the schema gutter should take room from the rows");
    });

    /// <summary>A right-click on a row selects its node and opens the node menu, instead of
    /// copying the value outright.</summary>
    [Fact]
    public Task RightClickingARowSelectsItAndOpensTheNodeMenu() => WithView("""{"a":1,"b":[1,2],"c":"x"}""", async (window, vm, surface) =>
    {
        // The second row: "a": 1.
        var point = surface.TranslatePoint(new Point(RowSurface.ContentPaddingX + 60, RowSurface.RowHeight * 1.5), window)!.Value;

        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);
        await PumpAsync();

        Assert.Equal("$.a", vm.SelectedPath);
        Assert.Equal(["Copy value", "Copy JSONPath", "Show in text view"], OpenMenuHeaders(window));
    });

    /// <summary>The headers of every menu item on screen: an open flyout's items are realised in
    /// a popup, which the headless platform hosts in the window's overlay layer.</summary>
    private static string[] OpenMenuHeaders(Window window) =>
        window.GetVisualDescendants().OfType<MenuItem>()
            .Select(menuItem => menuItem.Header as string ?? string.Empty)
            .ToArray();
}
