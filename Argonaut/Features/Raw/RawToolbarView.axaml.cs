using System;
using System.Threading.Tasks;
using Argonaut.Engine.Transforms;
using Argonaut.Features.Raw.Recipes;
using Avalonia.Controls;

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

    /// <summary>
    /// Builds the Transform menu afresh each time it opens: the encodings, then the saved recipes
    /// (forward, with a submenu to run one in reverse), then the recipe dialog. Each item replaces
    /// the whole document as one undoable edit.
    /// </summary>
    private void OnTransformMenuOpening(object? sender, EventArgs e)
    {
        if (sender is not MenuFlyout menu)
            return;

        var items = menu.Items;
        items.Clear();
        if (DataContext is not RawToolbarViewModel { Transforms: { } transforms })
            return;

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
            return;

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
