using Argonaut.Engine.Bytes;
using Argonaut.Features.Hex;
using Argonaut.Tests.Support;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Argonaut.Tests.Features.Hex;

/// <summary>The hex view as shown. Harness rule (docs/headless-test-dispatch-hole.md): the
/// dispatch body returns true and the test returns the dispatch task.</summary>
public sealed class HexViewTests
{
    [Fact]
    public Task OpeningADocument_FocusesTheDump_NotTheChrome()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HexViewTests).Assembly);
        return session.Dispatch(async () =>
        {
            using var origin = new MemoryByteOrigin(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(), "bytes");
            using var hex = new HexViewModel();
            await hex.LoadAsync(origin);

            // Something focusable ahead of the view, as the title bar's close button is.
            var chrome = new Button { Content = "x" };
            var view = new HexView { DataContext = hex };
            var window = new Window { Width = 800, Height = 400, Content = new DockPanel { Children = { chrome, view } } };
            DockPanel.SetDock(chrome, Dock.Top);
            try
            {
                window.Show();
                await Task.Delay(30);
                Dispatcher.UIThread.RunJobs();

                var surface = view.GetVisualDescendants().OfType<HexSurface>().Single();
                Assert.True(surface.IsFocused);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    private static readonly RawInputModifiers Command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    /// <summary>A shown hex view over 5000 counting bytes, inside a window whose own bubbling
    /// Ctrl/Cmd+L handler stands in for the shell's new request.</summary>
    private static Task WithView(Func<Window, HexView, HexViewModel, Func<int>, Task> body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HexViewTests).Assembly);
        return session.Dispatch(async () =>
        {
            using var origin = new MemoryByteOrigin(Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray(), "bytes");
            using var hex = new HexViewModel();
            await hex.LoadAsync(origin);

            var view = new HexView { DataContext = hex };
            var window = new Window { Width = 800, Height = 400, Content = view };
            int newRequests = 0;
            window.AddHandler(InputElement.KeyDownEvent, (_, e) =>
            {
                if (e.Key == Key.L)
                    newRequests++;
            }, RoutingStrategies.Bubble);

            try
            {
                window.Show();
                await Task.Delay(30);
                Dispatcher.UIThread.RunJobs();
                await body(window, view, hex, () => newRequests);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public Task TheStructTab_DrawsTheTree_AndACaretMoveKeepsIt_ReadingFromTheNewPlace() => WithView((window, view, hex, _) =>
    {
        hex.Structs.DefinitionText = "struct Pair { uint8_t a; uint8_t b; };";
        hex.SidePanelTab = HexPanelTab.Struct;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var surface = view.GetVisualDescendants().OfType<Argonaut.Ui.Tree.TreeSurface>().Single(s => s.Name == "StructSurface");
        var tree = hex.Structs.Tree!;
        Assert.Same(tree, surface.Document);
        Assert.Equal(3, surface.RealizedRows.Count);

        // Following the caret moves the same tree, drawn again without starting over.
        hex.Cursor.Place(0x40);
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        Assert.Same(tree, surface.Document);
        Assert.Equal(0x40, tree.Start);
        Assert.Equal("64", tree.ValueOf(tree.Nodes.Child(tree.Nodes.Root, 0)).Text);

        // A long selection is an array: a new tree.
        hex.Cursor.Select(new HexCaret(0x40, 0x46));
        Dispatcher.UIThread.RunJobs();
        Assert.NotSame(tree, surface.Document);
        Assert.Same(hex.Structs.Tree, surface.Document);
        return Task.CompletedTask;
    });

    [Fact]
    public Task TheTabShown_IsRemembered() => WithView((_, _, hex, _) =>
    {
        hex.ShowsStructuresTab = true;
        Assert.Equal(HexPanelTab.Structures, hex.SidePanelTab);
        Assert.False(hex.ShowsValuesTab);
        return Task.CompletedTask;
    });

    private static TextBox Entry(HexView view) => view.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "GoToEntry");

    private static HexSurface Surface(HexView view) => view.GetVisualDescendants().OfType<HexSurface>().Single();

    private static Border SidePanel(HexView view) => view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SidePanel");

    private static void Relayout(Window window, double width)
    {
        window.Width = width;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    [Fact]
    public Task AWideWindow_KeepsTheDumpAtItsDrawnWidth_AndTheSidePanelTakesTheRest() => WithView((window, view, _, _) =>
    {
        Relayout(window, 2000);

        var surface = Surface(view);
        var panel = SidePanel(view);
        Assert.Equal(surface.ContentWidth, surface.Bounds.Width, 0.5);
        Assert.True(panel.Bounds.Width > HexView.SidePanelMinWidth);
        Assert.Equal(view.Bounds.Width, panel.Bounds.Right, 0.5);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ANarrowWindow_KeepsTheSidePanelsMinimum_AndTheDumpGivesUpTheRoom() => WithView((window, view, _, _) =>
    {
        Relayout(window, 2000);
        Relayout(window, 700);

        var surface = Surface(view);
        Assert.Equal(HexView.SidePanelMinWidth, SidePanel(view).Bounds.Width, 0.5);
        Assert.True(surface.Bounds.Width < surface.ContentWidth);
        return Task.CompletedTask;
    });

    [Fact]
    public Task CommandL_OpensTheGoToEntry_AtTheCaret_AndTheWindowNeverSeesIt() => WithView((window, view, hex, newRequests) =>
    {
        hex.Cursor.Place(0x4D2);
        window.KeyPress(Key.L, Command, PhysicalKey.L, string.Empty);
        Dispatcher.UIThread.RunJobs();

        var entry = Entry(view);
        Assert.True(entry.IsVisible);
        Assert.True(entry.IsFocused);
        Assert.Equal("0x4D2", entry.Text);
        Assert.Equal(0, newRequests());
        return Task.CompletedTask;
    });

    [Fact]
    public Task Enter_GoesThere_AndGivesFocusBackToTheDump() => WithView((window, view, hex, _) =>
    {
        window.KeyPress(Key.L, Command, PhysicalKey.L, string.Empty);
        Dispatcher.UIThread.RunJobs();
        Entry(view).Text = "4000";
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, string.Empty);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(HexCaret.At(4000), hex.Cursor.Caret);
        Assert.False(Entry(view).IsVisible);
        Assert.True(Surface(view).IsFocused);
        return Task.CompletedTask;
    });

    [Fact]
    public Task AnEntryThatIsNoOffset_StaysOpen_AndEscapeLetsItGo() => WithView((window, view, hex, _) =>
    {
        window.KeyPress(Key.L, Command, PhysicalKey.L, string.Empty);
        Dispatcher.UIThread.RunJobs();
        Entry(view).Text = "9999999";
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, string.Empty);
        Dispatcher.UIThread.RunJobs();

        Assert.True(Entry(view).IsVisible);
        Assert.Equal(HexCaret.At(0), hex.Cursor.Caret);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, string.Empty);
        Dispatcher.UIThread.RunJobs();
        Assert.False(Entry(view).IsVisible);
        Assert.True(Surface(view).IsFocused);
        return Task.CompletedTask;
    });

    /// <summary>The shell's new request bubbles, relying on a focused text box - the find bar's,
    /// the request pane's - leaving Ctrl/Cmd+L unhandled.</summary>
    [Fact]
    public Task AFocusedTextBox_LetsCommandLThrough_ToTheWindow()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HexViewTests).Assembly);
        return session.Dispatch(async () =>
        {
            var box = new TextBox();
            var window = new Window { Content = box };
            int reached = 0;
            window.AddHandler(InputElement.KeyDownEvent, (_, e) => reached += e.Key == Key.L ? 1 : 0, RoutingStrategies.Bubble);
            window.Show();
            await Task.Delay(30);
            Dispatcher.UIThread.RunJobs();
            box.Focus();
            Dispatcher.UIThread.RunJobs();

            window.KeyPress(Key.L, Command, PhysicalKey.L, string.Empty);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, reached);
            window.Close();
            return true;
        }, CancellationToken.None);
    }
}
