using System.Text;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Xml;
using Argonaut.Tests.Support;
using Argonaut.Ui.Tree;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Argonaut.Tests.Features.Xml;

/// <summary>
/// The XML view end to end in a headless window: a loaded document draws rows, a reveal (what a
/// search hit or a path jump does) selects the row holding the offset and fills in its XPath, and
/// the path bar speaks XPath. Harness rule (docs/headless-test-dispatch-hole.md): the dispatch
/// body returns true and the test returns the dispatch task.
/// </summary>
public sealed class XmlViewTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"view-{Guid.NewGuid():N}.xml");

    public void Dispose() => File.Delete(path);

    private static async Task PumpAsync()
    {
        await Task.Delay(30);
        Dispatcher.UIThread.RunJobs();
    }

    private Task WithView(string xml, Func<Window, XmlViewModel, TreeSurface, Task> body)
    {
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(xml));
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(XmlViewTests).Assembly);
        return session.Dispatch(async () =>
        {
            var vm = new XmlViewModel(new XmlViewSettings());
            var window = new Window { Width = 900, Height = 500 };
            try
            {
                await vm.LoadAsync(path);
                await vm.IndexingTask;
                window.Content = new XmlView { DataContext = vm };
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
                vm.Dispose();
            }

            return true;
        }, CancellationToken.None);
    }

    private static string BigDocument()
    {
        var xml = new StringBuilder("<?xml version=\"1.0\"?>\n<root xmlns:g=\"urn:g\">\n  <g:name>test</g:name>\n");
        for (int i = 0; i < 20_000; i++)
            xml.Append($"  <item id=\"{i}\"><g:tag>a</g:tag><child><deep>v{i}</deep></child></item>\n");
        return xml.Append("</root>\n").ToString();
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
    public Task ARevealSelectsTheRowAndFillsInItsXPath() => WithView(BigDocument(), async (_, vm, surface) =>
    {
        string xml = File.ReadAllText(path);
        long target = xml.IndexOf("<deep>v15000<", StringComparison.Ordinal);

        vm.Reveal(target);
        await PumpAsync();

        Assert.Equal(target, surface.SelectedRow!.Value.Node.ValueStart);
        Assert.Equal("/root/item[15001]/child/deep", vm.SelectedPath);
        Assert.Contains(surface.SelectedRow.Value.Key, surface.RealizedRows.Select(r => r.Key));

        // Up the path bar: the "item" segment selects the element's own row.
        var item = vm.SelectedPathSegments[1];
        vm.Reveal(item.Target);
        await PumpAsync();
        Assert.Equal("/root/item[15001]", vm.SelectedPath);
        Assert.Equal(TreeRowShape.Open, surface.SelectedRow!.Value.Shape);
    });

    [Fact]
    public Task AMatchInAnAttributeRevealsItsElement() => WithView(BigDocument(), async (_, vm, surface) =>
    {
        string xml = File.ReadAllText(path);
        long target = xml.IndexOf("id=\"12345\"", StringComparison.Ordinal) + 4;

        vm.RevealMatch(target);
        await PumpAsync();

        Assert.Equal(xml.LastIndexOf("<item", (int)target, StringComparison.Ordinal), surface.SelectedRow!.Value.Node.ValueStart);
        Assert.IsType<TreeSearchNavigator>(vm.CreateSearchNavigator());
    });

    [Fact]
    public Task ThePathBarSpeaksXPath() => WithView(BigDocument(), (window, _, _) =>
    {
        var bar = window.GetVisualDescendants().OfType<TreePathBar>().Single();

        Assert.Contains("XPath", bar.GoToTooltip);
        Assert.Contains("XPath", bar.CopyPathTooltip);
        Assert.StartsWith("/", bar.EntryPlaceholder);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("<?xml version=\"1.0\" encoding=\"ISO-8859-1\"?><a>café</a>", true)]
    [InlineData("<?xml version='1.0' encoding='windows-1252'?><a/>", true)]
    [InlineData("<?xml version=\"1.0\" encoding=\"UTF-8\"?><a/>", false)]
    [InlineData("<?xml version=\"1.0\" encoding=\"utf-8\"?><a/>", false)]
    [InlineData("<?xml version=\"1.0\" encoding=\"US-ASCII\"?><a/>", false)]
    [InlineData("<a/>", false)]
    public async Task ADeclaredEncodingOtherThanUtf8IsNoted(string xml, bool noted)
    {
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(xml));
        using var vm = new XmlViewModel(new XmlViewSettings());
        await vm.LoadAsync(path);
        await vm.IndexingTask;

        Assert.Equal(noted, vm.StatusDetail?.Contains("shown as UTF-8", StringComparison.Ordinal) ?? false);
    }
}
