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

/// <summary>
/// The Transform menu is built on each click, so only clicking it in a real toolbar proves it
/// opens with something in it - and is drawn with it.
///
/// Every dispatched body ends <c>return true</c>: without a result the async lambda binds to
/// <c>Dispatch&lt;Task&gt;</c>, whose task completes at the body's first await, and every
/// assertion after it goes unobserved.
/// </summary>
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
            return true;
        }, CancellationToken.None);
    }

    private static async Task<(RawViewModel Vm, RawToolbarView Toolbar, Window Window)> ShownAsync(string content)
    {
        var store = new RecipeFolderStore(RecipeFolderStore.BundledDirectoryBesideApp,
            Path.Combine(Path.GetTempPath(), "ArgonautTests", Guid.NewGuid().ToString("N")));
        var vm = new RawViewModel(new RawViewSettings(), recipes: store);
        await vm.LoadAsync(new MemoryByteOrigin(Encoding.UTF8.GetBytes(content), "Pasted text"));
        await vm.IndexingTask;
        vm.SetEditing(true);

        var toolbar = new RawToolbarView { DataContext = vm.Toolbar };
        var window = new Window { Content = toolbar };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (vm, toolbar, window);
    }

    private static MenuFlyout Open(RawToolbarView toolbar, Window window)
    {
        var button = toolbar.FindControl<Button>("TransformButton")!;
        var centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, Avalonia.Input.MouseButton.Left);
        window.MouseUp(centre, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        return toolbar.TransformMenu!;
    }

    private static string DocumentText(RawViewModel vm)
    {
        var bytes = new byte[vm.Document!.AvailableLength];
        vm.Document.CopyTo(0, bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    [Theory]
    [InlineData("Comma-separated list", "a, b")]
    [InlineData("SQL IN list", "(\n  'a',\n  'b'\n)")]
    public Task EachPreset_FromTheMenu_TransformsTheDocument(string preset, string expected)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RawToolbarTransformMenuTests).Assembly);
        return session.Dispatch(async () =>
        {
            var (vm, toolbar, window) = await ShownAsync("a\nb\n");
            using var _ = vm;
            var menu = Open(toolbar, window);

            var item = menu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == preset);
            item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            await WaitAsync(() => !vm.IsTransforming && DocumentText(vm) != "a\nb\n");

            Assert.Equal(expected, DocumentText(vm));
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public Task EditRecipes_OpensTheDialog()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RawToolbarTransformMenuTests).Assembly);
        return session.Dispatch(async () =>
        {
            var (vm, toolbar, window) = await ShownAsync("a\nb\n");
            using var _ = vm;
            var menu = Open(toolbar, window);

            var item = menu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == "Edit recipes…");
            item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            var dialog = Assert.Single(window.OwnedWindows.OfType<Argonaut.Features.Raw.Recipes.RecipeEditorDialog>());
            dialog.Close();
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }
}
