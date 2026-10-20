using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Recipes;
using Argonaut.Features.Raw;

namespace Argonaut.Tests.Features.Raw;

/// <summary>Applying a recipe to the raw document: one undoable replacement, and nothing
/// changed when the recipe cannot read the input.</summary>
public class RawRecipeTests
{
    private static readonly Recipe SqlInList = new()
    {
        Template = "'{line}'",
        Escape = TextEscape.Sql,
        Separator = ",\n",
        Open = "(\n",
        Close = "\n)",
    };

    private static string DocumentText(RawViewModel vm)
    {
        var source = vm.Document!;
        var bytes = new byte[source.AvailableLength];
        source.CopyTo(0, bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    private static async Task<RawViewModel> LoadedAsync(string content)
    {
        var vm = new RawViewModel(new RawViewSettings());
        await vm.LoadAsync(new MemoryByteOrigin(Encoding.UTF8.GetBytes(content), "Pasted text"));
        await vm.IndexingTask;
        return vm;
    }

    [Fact]
    public async Task Apply_ReplacesTheDocument_EnteringEditMode_AndUndoGivesTheSourceBack()
    {
        using var vm = await LoadedAsync("a@x.com\nb@y.com\n");
        Assert.False(vm.IsEditing);

        Assert.True(await vm.ApplyRecipeAsync(SqlInList, RecipeDirection.Forward));

        Assert.True(vm.IsEditing);
        Assert.True(vm.IsDirty);
        Assert.False(vm.IsApplyingRecipe);
        Assert.Equal("(\n'a@x.com',\n'b@y.com'\n)", DocumentText(vm));
        Assert.Equal(4, vm.RowCount);

        Assert.True(vm.Undo());
        Assert.Equal("a@x.com\nb@y.com\n", DocumentText(vm));
    }

    [Fact]
    public async Task Apply_InReverse_TakesTheListApart()
    {
        using var vm = await LoadedAsync("('a', 'b')");

        Assert.True(await vm.ApplyRecipeAsync(SqlInList, RecipeDirection.Reverse));

        Assert.Equal("a\nb", DocumentText(vm));
    }

    [Fact]
    public async Task ARecipeThatCannotReadTheInput_LeavesTheDocumentAlone()
    {
        using var vm = await LoadedAsync("not base64 at all!");
        var decode = new Recipe { Kind = RecipeKind.Codec, Codec = RecipeCodec.Base64, Scope = RecipeScope.WholeText };

        Assert.False(await vm.ApplyRecipeAsync(decode, RecipeDirection.Reverse));

        Assert.Equal("not base64 at all!", DocumentText(vm));
        Assert.False(vm.IsDirty);
        Assert.False(vm.IsApplyingRecipe);
    }

    [Fact]
    public async Task TwoRecipesInARow_UndoOneAtATime()
    {
        using var vm = await LoadedAsync("a\nb\n");
        var commaList = new Recipe { Separator = "," };

        await vm.ApplyRecipeAsync(commaList, RecipeDirection.Forward);
        await vm.ApplyRecipeAsync(SqlInList, RecipeDirection.Forward);
        Assert.Equal("(\n'a,b'\n)", DocumentText(vm));

        vm.Undo();
        Assert.Equal("a,b", DocumentText(vm));
        vm.Undo();
        Assert.Equal("a\nb\n", DocumentText(vm));
    }
}
