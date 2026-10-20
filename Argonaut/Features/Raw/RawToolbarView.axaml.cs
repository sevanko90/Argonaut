using System;
using System.Threading.Tasks;
using Argonaut.Engine.Transforms;
using Argonaut.Features.Raw.Recipes;
using Avalonia.Controls;
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
        item.Click += (_, _) => _ = action();
        return item;
    }

    private async Task<bool> EditRecipesAsync(RawTransformMenuViewModel transforms)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner || transforms.CreateEditor() is not { } editor)
            return false;

        return await RecipeEditorDialog.ShowAsync(owner, editor) && await transforms.ApplyFromEditorAsync(editor);
    }
}
