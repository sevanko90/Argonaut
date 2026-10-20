using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Transforms;
using Argonaut.Features.Raw.Recipes;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Features.Raw.Recipes;

public class RecipeEditorViewModelTests
{
    private static readonly Recipe SqlInList = new()
    {
        Description = "Quotes each line.",
        Template = "'{line}'",
        Escape = TextEscape.Sql,
        Separator = ",\n",
        Open = "(",
        Close = ")",
    };

    private static readonly Recipe CommaList = new() { Separator = ", " };

    private static InMemoryRecipeStore Store() => new(("SQL IN list", SqlInList), ("Comma-separated list", CommaList));

    private static RecipeEditorViewModel Editor(string document, InMemoryRecipeStore? store = null) =>
        new(store ?? Store(), new MemoryByteSource(Encoding.UTF8.GetBytes(document)));

    [Fact]
    public void Opens_OnTheFirstRecipe_WithItsSettingsInDisplayForm()
    {
        var editor = Editor("a\nb\n");

        Assert.Equal("Comma-separated list", editor.SelectedEntry!.Value.DisplayName);

        editor.SelectedEntry = editor.Entries.Single(e => e.DisplayName == "SQL IN list");

        Assert.Equal("'{line}'", editor.Template);
        Assert.Equal(@",\n", editor.Separator);
        Assert.Equal((int)TextEscape.Sql, editor.EscapeIndex);
        Assert.Equal("Quotes each line.", editor.Description);
        Assert.False(editor.IsModified);
    }

    [Fact]
    public void Preview_ShowsTheResult_AndFollowsEveryChange()
    {
        var editor = Editor("a\nb\n");
        Assert.Equal("a, b", editor.PreviewText);

        editor.Separator = @"\n";

        Assert.Equal("a\nb", editor.PreviewText);
        Assert.True(editor.IsModified);
        Assert.Equal("\n", editor.CurrentRecipe.Separator);
    }

    [Fact]
    public void Reverse_PreviewsTakingTheListApart_AndRelabelsApply()
    {
        var editor = Editor("x, y, z");

        editor.Reverse = true;

        Assert.Equal("x\ny\nz", editor.PreviewText);
        Assert.Equal("Reverse", editor.ApplyLabel);
        Assert.Equal(TransformDirection.Reverse, editor.Direction);
        Assert.False(editor.IsModified);
    }

    [Fact]
    public void AnInputTheRecipeCannotRead_IsAProblem_NotAPreview()
    {
        var editor = Editor("plain\n");
        editor.SelectedEntry = editor.Entries.Single(e => e.DisplayName == "SQL IN list");

        editor.Reverse = true;

        Assert.StartsWith("Line 1:", editor.PreviewProblem);
        Assert.Equal("", editor.PreviewText);
    }

    [Fact]
    public void Preview_ReadsOnlyTheStartOfALongDocument()
    {
        string document = string.Concat(Enumerable.Range(0, 200).Select(i => $"line{i}\n"));

        var editor = Editor(document);

        Assert.Equal($"Preview · first {RecipeEditorViewModel.PreviewLines} lines", editor.PreviewCaption);
        Assert.EndsWith($"line{RecipeEditorViewModel.PreviewLines - 1}", editor.PreviewText);
    }

    [Fact]
    public void AShortDocument_IsPreviewedWhole()
    {
        Assert.Equal("Preview · whole document", Editor("a\nb").PreviewCaption);
    }

    [Fact]
    public void Save_AddsTheUsersRecipe_AndSelectsIt()
    {
        var store = Store();
        var editor = Editor("a\nb\n", store);
        editor.Separator = "|";
        editor.SaveName = "Pipes";

        Assert.True(editor.Save());

        Assert.Equal("Pipes", editor.SelectedEntry!.Value.DisplayName);
        Assert.True(editor.SelectedEntry.Value.IsUser);
        Assert.True(editor.CanDelete);
        Assert.False(editor.IsModified);
        Assert.Equal("|", store.Load(editor.SelectedEntry.Value)!.Separator);
    }

    [Fact]
    public void Save_WithoutAName_SaysSo()
    {
        var editor = Editor("a");
        editor.SaveName = "  ";

        Assert.False(editor.Save());
        Assert.NotNull(editor.SaveProblem);
    }

    [Fact]
    public void Delete_RemovesTheUsersRecipe_AndPresetsCannotBeDeleted()
    {
        var store = Store();
        var editor = Editor("a", store);
        Assert.False(editor.CanDelete);

        editor.SaveName = "Mine";
        editor.Save();
        editor.Delete();

        Assert.DoesNotContain(editor.Entries, e => e.DisplayName == "Mine");
        Assert.Equal("Comma-separated list", editor.SelectedEntry!.Value.DisplayName);
    }
}
