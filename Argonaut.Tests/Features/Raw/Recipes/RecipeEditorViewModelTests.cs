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

    private static RecipeListRow Row(RecipeEditorViewModel editor, string name, bool isUser = false) =>
        editor.Rows.Single(r => r.Entry is { } e && e.DisplayName == name && e.IsUser == isUser);

    private static string[] Listed(RecipeEditorViewModel editor) => editor.Rows.Select(r => r.Text).ToArray();

    [Fact]
    public void Lists_PresetsThenYours_UnderHeadings_AndOpensOnTheFirstRecipe()
    {
        var store = Store();
        store.Save("Pipes", new Recipe { Separator = "|" });

        var editor = Editor("a\nb\n", store);

        Assert.Equal(["Presets", "Comma-separated list", "SQL IN list", "Yours", "Pipes"], Listed(editor));
        Assert.Equal("Comma-separated list", editor.Name);
    }

    [Fact]
    public void WithNoRecipesOfYourOwn_ThereIsNoYoursHeading()
    {
        Assert.DoesNotContain("Yours", Listed(Editor("a")));
    }

    [Fact]
    public void AHeading_CannotBeSelected()
    {
        var editor = Editor("a");
        var before = editor.SelectedRow;

        editor.SelectedRow = editor.Rows.First(r => r.IsHeading);

        Assert.Same(before, editor.SelectedRow);
    }

    [Fact]
    public void Selecting_LoadsTheSettingsInDisplayForm_AndPreviewsThem()
    {
        var editor = Editor("a\nb\n");

        editor.SelectedRow = Row(editor, "SQL IN list");

        Assert.Equal("'{line}'", editor.Template);
        Assert.Equal(@",\n", editor.Separator);
        Assert.Equal((int)TextEscape.Sql, editor.EscapeIndex);
        Assert.Equal("Quotes each line.", editor.Description);
        Assert.Equal("('a',\n'b')", editor.PreviewText);
        Assert.Equal("1\n2", editor.PreviewLineNumbers);
        Assert.False(editor.IsModified);
        Assert.False(editor.IsUserRecipe);
    }

    [Fact]
    public void Preview_FollowsEveryChange()
    {
        var editor = Editor("a\nb\n");
        Assert.Equal("a, b", editor.PreviewText);

        editor.Separator = @"\n";

        Assert.Equal("a\nb", editor.PreviewText);
        Assert.True(editor.IsModified);
        Assert.Equal("\n", editor.CurrentRecipe.Separator);
    }

    [Fact]
    public void TheDirectionSwitch_PreviewsTakingTheListApart_AndRelabelsApply()
    {
        var editor = Editor("x, y, z");

        editor.IsForward = false;

        Assert.True(editor.Reverse);
        Assert.Equal("x\ny\nz", editor.PreviewText);
        Assert.Equal("Take apart", editor.ApplyLabel);
        Assert.Equal(TransformDirection.Reverse, editor.Direction);
        Assert.False(editor.IsModified);
    }

    [Fact]
    public void AnInputTheRecipeCannotRead_IsAProblem_NotAPreview()
    {
        var editor = Editor("plain\n");
        editor.SelectedRow = Row(editor, "SQL IN list");

        editor.Reverse = true;

        Assert.StartsWith("Line 1:", editor.PreviewProblem);
        Assert.Equal("", editor.PreviewText);
    }

    [Fact]
    public void Preview_ReadsOnlyTheStartOfALongDocument()
    {
        string document = string.Concat(Enumerable.Range(0, 200).Select(i => $"line{i}\n"));

        var editor = Editor(document);

        Assert.Equal($"first {RecipeEditorViewModel.PreviewLines} lines", editor.PreviewCaption);
        Assert.EndsWith($"line{RecipeEditorViewModel.PreviewLines - 1}", editor.PreviewText);
    }

    [Fact]
    public void AShortDocument_IsPreviewedWhole()
    {
        Assert.Equal("whole document", Editor("a\nb").PreviewCaption);
    }

    [Fact]
    public void Revert_PutsTheSavedSettingsBack()
    {
        var editor = Editor("a\nb\n");
        editor.Separator = "|";

        editor.Revert();

        Assert.Equal(", ", editor.Separator);
        Assert.False(editor.IsModified);
    }

    [Fact]
    public void ChoosingAnotherRecipe_DropsTheDraft()
    {
        var editor = Editor("a\nb\n");
        editor.Separator = "|";

        editor.SelectedRow = Row(editor, "SQL IN list");
        editor.SelectedRow = Row(editor, "Comma-separated list");

        Assert.Equal(", ", editor.Separator);
    }

    [Fact]
    public void SavingAPresetsChanges_SavesYourOwn_WhichStandsInForThePreset()
    {
        var store = Store();
        var editor = Editor("a\nb\n", store);
        editor.Separator = "|";

        Assert.True(editor.SaveChanges());

        Assert.Equal(["Presets", "SQL IN list", "Yours", "Comma-separated list"], Listed(editor));
        Assert.True(editor.IsUserRecipe);
        Assert.False(editor.IsModified);
        Assert.Equal("|", store.Load(editor.SelectedEntry!.Value)!.Separator);
    }

    [Fact]
    public void DeletingYourOwn_BringsBackThePresetItStoodInFor()
    {
        var editor = Editor("a\nb\n");
        editor.Separator = "|";
        editor.SaveChanges();

        editor.Delete();

        Assert.Equal(["Presets", "Comma-separated list", "SQL IN list"], Listed(editor));
        Assert.Equal(", ", editor.Separator);
    }

    [Fact]
    public void CreateFromCurrent_AddsYourOwnUnderAFreeName_WithTheCurrentSettings()
    {
        var editor = Editor("a\nb\n");
        editor.Separator = "|";

        Assert.True(editor.CreateFromCurrent());
        Assert.True(editor.CreateFromCurrent());

        Assert.Equal(RecipeEditorViewModel.NewRecipeName + " 2", editor.Name);
        Assert.Contains(RecipeEditorViewModel.NewRecipeName, Listed(editor));
        Assert.True(editor.IsUserRecipe);
        Assert.Equal("|", editor.Separator);
        Assert.Null(editor.Description);
    }

    [Fact]
    public void Rename_MovesYourRecipe_KeepingAnUnsavedDraft()
    {
        var store = Store();
        var editor = Editor("a\nb\n", store);
        editor.CreateFromCurrent();
        editor.Separator = ";";

        editor.Name = "Semicolons";
        Assert.True(editor.Rename());

        Assert.Equal("Semicolons", editor.Name);
        Assert.DoesNotContain(RecipeEditorViewModel.NewRecipeName, Listed(editor));
        Assert.Equal(";", editor.Separator);
        Assert.True(editor.IsModified);
    }

    [Fact]
    public void Rename_ToAnotherOfYours_IsRefused()
    {
        var store = Store();
        store.Save("Pipes", new Recipe { Separator = "|" });
        var editor = Editor("a", store);
        editor.CreateFromCurrent();

        editor.Name = "pipes";

        Assert.False(editor.Rename());
        Assert.NotNull(editor.StoreProblem);
        Assert.Equal(RecipeEditorViewModel.NewRecipeName, editor.Name);
    }

    [Fact]
    public void APreset_CannotBeRenamedOrDeleted()
    {
        var editor = Editor("a");
        editor.Name = "Something else";

        Assert.False(editor.Rename());
        editor.Delete();

        Assert.Contains("Comma-separated list", Listed(editor));
    }
}
