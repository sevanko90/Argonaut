using System;
using System.Threading.Tasks;
using Argonaut.Engine.Transforms;
using Argonaut.Features.Raw.Recipes;
using Argonaut.Ui.Notifications;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;

namespace Argonaut.Features.Raw;

public partial class RawToolbarView : UserControl
{
    private static readonly (TextCodec Codec, string Name)[] Codecs =
    [
        (TextCodec.JsonString, "JSON string"),
        (TextCodec.Url, "URL"),
        (TextCodec.Base64, "Base64"),
        (TextCodec.Base64Url, "Base64 (URL-safe)"),
    ];

    public RawToolbarView()
    {
        InitializeComponent();
    }

    /// <summary>The Transform menu last shown, for tests to look inside.</summary>
    internal MenuFlyout? TransformMenu { get; private set; }

    /// <summary>
    /// Builds the whole menu, then shows it - never filled in while opening, which left the real
    /// app's popup measured empty: a sliver with nothing in it.
    /// </summary>
    private void OnTransformClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RawToolbarViewModel { Transforms: { } transforms })
            return;

        TransformMenu = BuildTransformMenu(transforms);
        TransformMenu.ShowAt(TransformButton);
    }

    /// <summary>
    /// The Transform menu: the encodings, then the saved recipes (forward, with a submenu to run
    /// one in reverse), then the recipe dialog. Each item replaces the whole document as one
    /// undoable edit.
    /// </summary>
    private MenuFlyout BuildTransformMenu(RawTransformMenuViewModel transforms)
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        var items = menu.Items;

        items.Add(CodecMenu("Encode", transforms, TransformDirection.Forward));
        items.Add(CodecMenu("Decode", transforms, TransformDirection.Reverse));
        var eachLine = new MenuItem
        {
            Header = "Each line separately",
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = transforms.EachLine,
        };
        eachLine.Click += (_, _) => transforms.EachLine = !transforms.EachLine;
        items.Add(eachLine);

        if (!transforms.HasRecipes)
            return menu;

        items.Add(new Separator());
        var recipes = transforms.ListRecipes();
        var reverse = new MenuItem { Header = "Reverse a recipe" };
        foreach (var entry in recipes)
        {
            items.Add(Item(entry.DisplayName, () => transforms.ApplyRecipeAsync(entry, TransformDirection.Forward)));
            reverse.Items.Add(Item(entry.DisplayName, () => transforms.ApplyRecipeAsync(entry, TransformDirection.Reverse)));
        }

        if (recipes.Count > 0)
            items.Add(reverse);

        items.Add(new Separator());
        items.Add(Item("Edit recipes…", () => EditRecipesAsync(transforms)));
        return menu;
    }

    private static MenuItem CodecMenu(string header, RawTransformMenuViewModel transforms, TransformDirection direction)
    {
        var menu = new MenuItem { Header = header };
        foreach (var (codec, name) in Codecs)
            menu.Items.Add(Item(name, () => transforms.ApplyCodecAsync(codec, direction)));

        return menu;
    }

    private static MenuItem Item(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) =>
        {
            // Started from a click and not awaited by anyone, so a failure would otherwise vanish
            // with the task - and the menu would look like it had done nothing.
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                ToastService.Show($"{header} failed: {ex.Message}");
            }
        };
        return item;
    }

    private async Task<bool> EditRecipesAsync(RawTransformMenuViewModel transforms)
    {
        // The window this toolbar is in, or failing that the app's: by the time a menu item's
        // click runs, the menu that held it may have gone.
        var owner = TopLevel.GetTopLevel(this) as Window
            ?? (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null)
        {
            ToastService.Show("The recipe window could not be opened: no window to open it over.");
            return false;
        }

        if (transforms.CreateEditor() is not { } editor)
        {
            ToastService.Show("Recipes are not available for this document.");
            return false;
        }

        return await RecipeEditorDialog.ShowAsync(owner, editor) && await transforms.ApplyFromEditorAsync(editor);
    }
}
