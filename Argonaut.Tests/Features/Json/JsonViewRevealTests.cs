using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Schema;
using Argonaut.Tests.Support;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Argonaut.Tests.Features.Json;

/// <summary>
/// Headless: a node revealed in the JSON view is scrolled on screen, whichever happens first -
/// the view attaching or the reveal landing. A hop back from the text view publishes the
/// document and reveals the caret's node straight away, often before the view has been laid out
/// at all.
/// </summary>
public sealed class JsonViewRevealTests
{
    private static string WriteLongArray()
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < 5000; i++)
            sb.Append(i == 0 ? "" : ",\n").Append(i);
        sb.Append(']');

        string path = Path.GetTempFileName();
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    private static async Task PumpAsync(int milliseconds = 50)
    {
        await Task.Delay(milliseconds);
        Dispatcher.UIThread.RunJobs();
    }

    private static bool IsRealized(Window window, int tokenIndex) =>
        window.GetVisualDescendants().OfType<ListBoxItem>()
            .Any(item => item.DataContext is JsonRow { TokenIndex: var t } && t == tokenIndex);

    private static Task RevealTestAsync(bool revealBeforeAttach)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(JsonViewRevealTests).Assembly);
        return session.Dispatch(async () =>
        {
            string path = WriteLongArray();
            var vm = new JsonViewModel(new JsonViewSettings(), new SchemaBindings(), TestSchemas.Catalog());
            Window? window = null;
            try
            {
                await vm.LoadAsync(path);
                await vm.IndexingTask;
                long offset = File.ReadAllText(path).IndexOf("4000", StringComparison.Ordinal);

                if (revealBeforeAttach)
                    await vm.RevealByteRangeAsync(ByteRange.At(offset));

                window = new Window { Width = 900, Height = 600, Content = new JsonView { DataContext = vm } };
                window.Show();

                if (!revealBeforeAttach)
                {
                    await PumpAsync();
                    await vm.RevealByteRangeAsync(ByteRange.At(offset));
                }

                await PumpAsync();
                window.UpdateLayout();
                await PumpAsync();

                Assert.NotNull(vm.SelectedTokenIndex);
                Assert.True(IsRealized(window, vm.SelectedTokenIndex!.Value), "the revealed node was not scrolled on screen");
            }
            finally
            {
                window?.Close();
                vm.Dispose();
                File.Delete(path);
            }
        }, CancellationToken.None);
    }

    /// <summary>The shell's own shape: the window is already up, the document arrives as content
    /// resolved through a DataTemplate, and the reveal follows in the same dispatcher turn.</summary>
    [Fact]
    public Task Reveal_InTheTurnTheDocumentIsSwappedIn_ScrollsToTheNode()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(JsonViewRevealTests).Assembly);
        return session.Dispatch(async () =>
        {
            string path = WriteLongArray();
            var vm = new JsonViewModel(new JsonViewSettings(), new SchemaBindings(), TestSchemas.Catalog());
            var host = new ContentControl();
            var window = new Window { Width = 900, Height = 600, Content = host };
            window.DataTemplates.Add(new Avalonia.Controls.Templates.FuncDataTemplate<JsonViewModel>((_, _) => new JsonView()));
            try
            {
                window.Show();
                await PumpAsync();
                await vm.LoadAsync(path);
                await vm.IndexingTask;
                long offset = File.ReadAllText(path).IndexOf("4000", StringComparison.Ordinal);

                host.Content = vm;
                await vm.RevealByteRangeAsync(ByteRange.At(offset));

                await PumpAsync();
                window.UpdateLayout();
                await PumpAsync();

                Assert.True(IsRealized(window, vm.SelectedTokenIndex!.Value), "the revealed node was not scrolled on screen");
            }
            finally
            {
                window.Close();
                vm.Dispose();
                File.Delete(path);
            }
        }, CancellationToken.None);
    }

    /// <summary>What the app actually does: the view goes up after the first batch of tokens, and
    /// the reveal waits for the index to reach its offset while growth keeps landing.</summary>
    [Fact]
    public Task Reveal_WhileTheIndexIsStillGrowing_ScrollsToTheNode()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(JsonViewRevealTests).Assembly);
        return session.Dispatch(async () =>
        {
            var sb = new StringBuilder("{\"items\": [\n");
            for (int i = 0; i < 400_000; i++)
                sb.Append(i == 0 ? "" : ",\n").Append("  {\"id\": ").Append(i).Append(", \"name\": \"item").Append(i).Append("\"}");
            sb.Append("\n]}\n");
            string json = sb.ToString();
            string path = Path.GetTempFileName();
            File.WriteAllText(path, json);

            var vm = new JsonViewModel(new JsonViewSettings(), new SchemaBindings(), TestSchemas.Catalog());
            var host = new ContentControl();
            var window = new Window { Width = 900, Height = 600, Content = host };
            window.DataTemplates.Add(new Avalonia.Controls.Templates.FuncDataTemplate<JsonViewModel>((_, _) => new JsonView()));
            try
            {
                window.Show();
                await PumpAsync();
                await vm.LoadAsync(path);
                Assert.False(vm.IndexingTask.IsCompleted, "the fixture must still be indexing when the reveal starts");

                host.Content = vm;
                var reveal = vm.RevealByteRangeAsync(ByteRange.At(json.IndexOf("\"item300000\"", StringComparison.Ordinal) + 3));
                while (!reveal.IsCompleted)
                    await PumpAsync(20);

                for (int i = 0; i < 10; i++)
                {
                    await PumpAsync(100);
                    window.UpdateLayout();
                }

                Assert.Equal("$.items[300000].name", vm.SelectedPath);
                Assert.True(IsRealized(window, vm.SelectedTokenIndex!.Value), "the revealed node was not scrolled on screen");
            }
            finally
            {
                window.Close();
                vm.Dispose();
                File.Delete(path);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public Task Reveal_AfterTheViewIsShowing_ScrollsToTheNode() => RevealTestAsync(revealBeforeAttach: false);

    [Fact]
    public Task Reveal_BeforeTheViewIsLaidOut_ScrollsToTheNode() => RevealTestAsync(revealBeforeAttach: true);
}
