using Argonaut.Engine.Bytes;
using Argonaut.Features.Hex.Structs;
using Argonaut.Features.Hex.Values;
using Argonaut.Ui.Tree;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;

namespace Argonaut.Tests.Features.Hex.Structs;

/// <summary>The struct tree in the surface every tree view draws with: what it assumes of a row
/// source holds for one that is not a document's. Harness rule
/// (docs/headless-test-dispatch-hole.md): the dispatch body returns true.</summary>
public sealed class StructTreeSurfaceTests
{
    [Fact]
    public Task TheSurface_DrawsTheStructsRows_AndOpensAnArrayFromTheKeyboard()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(StructTreeSurfaceTests).Assembly);
        return session.Dispatch(async () =>
        {
            var definition = StructParser.Parse("struct P { int a; short v[1000]; char name[7]; };");
            var laid = StructLayoutPass.Lay(definition, "P", StructTarget.Lp64).Struct!;
            var tree = new StructTree(new StructNodes(laid, 1), new MemoryByteSource(new byte[laid.Size]), 0, ByteOrder.Little, StructTarget.Lp64);
            var surface = new TreeSurface { Document = tree };
            var window = new Window { Width = 600, Height = 300, Content = surface };
            try
            {
                window.Show();
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                Assert.Equal(5, surface.RealizedRows.Count);
                Assert.Equal(["P", "a", "v", "name", "padding"], surface.RealizedRows.Select(r => ((StructNode)r.Detail!).Label));

                // Clicking the root's row does not close it.
                surface.Toggle(surface.RealizedRows[0]);
                Assert.Equal(5, surface.RealizedRows.Count);

                // Down to the array, open it, and go to the end of it.
                surface.Focus();
                foreach (var key in new[] { Key.Down, Key.Down, Key.Down, Key.Right, Key.End })
                {
                    window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, string.Empty);
                    Dispatcher.UIThread.RunJobs();
                }

                window.UpdateLayout();
                window.CaptureRenderedFrame();
                Assert.Contains(surface.RealizedRows, r => ((StructNode)r.Detail!).Label == "[999]");
                Assert.Equal("padding", ((StructNode)surface.RealizedRows[^1].Detail!).Label);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }
}
