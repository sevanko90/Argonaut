using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Transforms;
using Argonaut.Features.Raw;

namespace Argonaut.Tests.Features.Raw;

/// <summary>Applying a recipe or codec to the raw document: one undoable replacement, and
/// nothing changed when the input cannot be read.</summary>
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

        Assert.True(await vm.ApplyRecipeAsync(SqlInList, TransformDirection.Forward));

        Assert.True(vm.IsEditing);
        Assert.True(vm.IsDirty);
        Assert.False(vm.IsTransforming);
        Assert.Equal("(\n'a@x.com',\n'b@y.com'\n)", DocumentText(vm));
        Assert.Equal(4, vm.RowCount);

        Assert.True(vm.Undo());
        Assert.Equal("a@x.com\nb@y.com\n", DocumentText(vm));
    }

    [Fact]
    public async Task Apply_InReverse_TakesTheListApart()
    {
        using var vm = await LoadedAsync("('a', 'b')");

        Assert.True(await vm.ApplyRecipeAsync(SqlInList, TransformDirection.Reverse));

        Assert.Equal("a\nb", DocumentText(vm));
    }

    [Fact]
    public async Task ACodecThatCannotReadTheInput_LeavesTheDocumentAlone()
    {
        using var vm = await LoadedAsync("not base64 at all!");

        Assert.False(await vm.ApplyCodecAsync(TextCodec.Base64, TransformDirection.Reverse, CodecScope.WholeText));

        Assert.Equal("not base64 at all!", DocumentText(vm));
        Assert.False(vm.IsDirty);
        Assert.False(vm.IsTransforming);
    }

    [Fact]
    public async Task ACodec_ReplacesTheDocument_AndUndoes()
    {
        using var vm = await LoadedAsync("hello");

        Assert.True(await vm.ApplyCodecAsync(TextCodec.Base64, TransformDirection.Forward, CodecScope.WholeText));
        Assert.Equal("aGVsbG8=", DocumentText(vm));

        Assert.True(vm.Undo());
        Assert.Equal("hello", DocumentText(vm));
    }

    [Fact]
    public async Task TwoRecipesInARow_UndoOneAtATime()
    {
        using var vm = await LoadedAsync("a\nb\n");
        var commaList = new Recipe { Separator = "," };

        await vm.ApplyRecipeAsync(commaList, TransformDirection.Forward);
        await vm.ApplyRecipeAsync(SqlInList, TransformDirection.Forward);
        Assert.Equal("(\n'a,b'\n)", DocumentText(vm));

        vm.Undo();
        Assert.Equal("a,b", DocumentText(vm));
        vm.Undo();
        Assert.Equal("a\nb\n", DocumentText(vm));
    }
}
