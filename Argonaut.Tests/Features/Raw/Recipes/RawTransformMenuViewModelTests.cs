using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Transforms;
using Argonaut.Features.Raw;
using Argonaut.Features.Raw.Recipes;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Features.Raw.Recipes;

public class RawTransformMenuViewModelTests
{
    private static async Task<RawViewModel> LoadedAsync(string content, IRecipeStore? recipes)
    {
        var vm = new RawViewModel(new RawViewSettings(), recipes: recipes);
        await vm.LoadAsync(new MemoryByteOrigin(Encoding.UTF8.GetBytes(content), "Pasted text"));
        await vm.IndexingTask;
        return vm;
    }

    private static RawTransformMenuViewModel Menu(RawViewModel vm) => ((RawToolbarViewModel)vm.Toolbar!).Transforms!;

    private static string DocumentText(RawViewModel vm)
    {
        var bytes = new byte[vm.Document!.AvailableLength];
        vm.Document.CopyTo(0, bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    [Fact]
    public async Task EachLine_EncodesLinesSeparately()
    {
        using var vm = await LoadedAsync("a\nbb\n", recipes: null);
        var menu = Menu(vm);
        menu.EachLine = true;

        Assert.True(await menu.ApplyCodecAsync(TextCodec.Base64, TransformDirection.Forward));

        Assert.Equal("YQ==\nYmI=\n", DocumentText(vm));
    }

    [Fact]
    public async Task ASavedRecipe_AppliesByEntry()
    {
        var store = new InMemoryRecipeStore(("Comma-separated list", new Recipe { Separator = ", " }));
        using var vm = await LoadedAsync("a\nb\n", store);
        var menu = Menu(vm);

        var entry = Assert.Single(menu.ListRecipes());
        Assert.True(await menu.ApplyRecipeAsync(entry, TransformDirection.Forward));

        Assert.Equal("a, b", DocumentText(vm));
    }

    [Fact]
    public async Task WithoutAStore_OnlyTheEncodingsAreOffered()
    {
        using var vm = await LoadedAsync("a", recipes: null);
        var menu = Menu(vm);

        Assert.False(menu.HasRecipes);
        Assert.Empty(menu.ListRecipes());
        Assert.Null(menu.CreateEditor());
    }

    [Fact]
    public async Task CanTransform_OnlyInEditMode()
    {
        using var vm = await LoadedAsync("a", recipes: null);
        var menu = Menu(vm);
        Assert.False(menu.CanTransform);

        vm.SetEditing(true);
        Assert.True(menu.CanTransform);

        vm.SetEditing(false);
        Assert.False(menu.CanTransform);
    }
}
