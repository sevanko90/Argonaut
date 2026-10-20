using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Transforms;
using Argonaut.Features.Raw;
using Argonaut.Tests.Support;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Argonaut.Tests.Features.Raw;

/// <summary>The Transform menu is built on each click, so only clicking it in a real toolbar
/// proves it opens with something in it - and is drawn with it.</summary>
public sealed class RawToolbarTransformMenuTests
{
    [Fact]
    public Task TheMenu_IsDisabledUntilEditing_ThenOpensWithTheEncodingsAndRecipes()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RawToolbarTransformMenuTests).Assembly);
        return session.Dispatch(async () =>
        {
            var store = new InMemoryRecipeStore(("Comma-separated list", new Recipe { Separator = ", " }));
            using var vm = new RawViewModel(new RawViewSettings(), recipes: store);
            await vm.LoadAsync(new MemoryByteOrigin(Encoding.UTF8.GetBytes("a\nb\n"), "Pasted text"));
            await vm.IndexingTask;

            var toolbar = new RawToolbarView { DataContext = vm.Toolbar };
            var window = new Window { Content = toolbar };
            window.Show();

            var button = toolbar.FindControl<Button>("TransformButton")!;
            Dispatcher.UIThread.RunJobs();
            Assert.False(button.IsEnabled);

            vm.SetEditing(true);
            Dispatcher.UIThread.RunJobs();
            Assert.True(button.IsEnabled);

            // Clicked, as the user does, rather than ShowAt: the flyout then opens through the
            // button's own path.
            var centre = button.TranslatePoint(new Avalonia.Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseDown(centre, Avalonia.Input.MouseButton.Left);
            window.MouseUp(centre, Avalonia.Input.MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            var menu = toolbar.TransformMenu!;
            Assert.True(menu.IsOpen);

            var headers = menu.Items.OfType<MenuItem>().Select(item => item.Header as string).ToList();
            Assert.Contains("Encode", headers);
            Assert.Contains("Decode", headers);
            Assert.Contains("Comma-separated list", headers);
            Assert.Contains("Edit recipes…", headers);

            var presenter = menu.Items.OfType<MenuItem>().First().FindAncestorOfType<MenuFlyoutPresenter>();
            Assert.NotNull(presenter);
            Assert.True(presenter.Bounds.Width > 50, $"menu drawn {presenter.Bounds.Width} wide");
            Assert.True(presenter.ItemCount >= 4, $"menu drawn with {presenter.ItemCount} items");

            menu.Hide();
            window.Close();
        }, CancellationToken.None);
    }
}
