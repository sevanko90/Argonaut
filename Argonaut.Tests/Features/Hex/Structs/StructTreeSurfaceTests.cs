using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Trees;
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

                Assert.Equal(4, surface.RealizedRows.Count);
                Assert.Equal(["a", "v", "name", "padding"], surface.RealizedRows.Select(r => ((StructNode)r.Detail!).Label));

                // Down to the array, open it, and go to the end of it.
                surface.Focus();
                foreach (var key in new[] { Key.Down, Key.Down, Key.Right, Key.End })
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

    [Fact]
    public Task ARowItsSourceKeepsOpen_HasNoArrow_AndDoesNotToggle()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(StructTreeSurfaceTests).Assembly);
        return session.Dispatch(async () =>
        {
            var definition = StructParser.Parse("struct In { int x; }; struct P { In in; int after; };");
            var laid = StructLayoutPass.Lay(definition, "P", StructTarget.Lp64).Struct!;
            var tree = new StructTree(new StructNodes(laid, 1), new MemoryByteSource(new byte[8]), 0, ByteOrder.Little, StructTarget.Lp64);
            var surface = new TreeSurface { Document = new KeepsFirstRowOpen(tree) };
            var window = new Window { Width = 400, Height = 200, Content = surface };
            try
            {
                window.Show();
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Assert.Equal(3, surface.RealizedRows.Count);

                surface.Toggle(surface.RealizedRows[0]);
                Assert.Equal(3, surface.RealizedRows.Count);
                window.CaptureRenderedFrame();
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    /// <summary>The struct tree, saying its first row - a nested struct - is always open.</summary>
    private sealed class KeepsFirstRowOpen(StructTree tree) : ITreeRowSource
    {
        public ITreeRowPainter Painter => tree.Painter;

        public IReadOnlyList<ITreeGutter> Gutters => tree.Gutters;

        public event EventHandler? Grew { add => tree.Grew += value; remove => tree.Grew -= value; }

        public event EventHandler? Closing { add => tree.Closing += value; remove => tree.Closing -= value; }

        public ITreeRowCursor NewCursor() => tree.NewCursor();

        public bool IsAlwaysOpen(in TreeRow row) => row.Start == tree.Nodes.Child(tree.Nodes.Root, 0).Position;

        public void Toggle(in TreeRow row) => tree.Toggle(row);

        public void SetExpanded(in TreeRow row, bool expanded) => tree.SetExpanded(row, expanded);

        public bool ExpandDeep(in TreeRow row, int rowBudget) => tree.ExpandDeep(row, rowBudget);

        public void CollapseDeep(in TreeRow row) => tree.CollapseDeep(row);

        public bool Hides(in TreeRow row, long position) => tree.Hides(row, position);

        public long ScrollLength => tree.ScrollLength;

        public long ScrollPosition(in TreeRow row) => tree.ScrollPosition(row);

        public void SeekScrollPosition(ITreeRowCursor cursor, long position) => tree.SeekScrollPosition(cursor, position);

        public bool IsComplete => tree.IsComplete;
    }
}
