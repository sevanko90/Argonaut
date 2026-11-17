using Argonaut.Engine.Bytes;
using Argonaut.Features.Hex;
using Argonaut.Tests.Support;
using Avalonia.Controls;
using Avalonia.Headless;
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
}
