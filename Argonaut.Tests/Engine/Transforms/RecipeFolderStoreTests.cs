using Argonaut.Engine.Transforms;

namespace Argonaut.Tests.Engine.Transforms;

/// <summary>The presets shipped beside the app, plus a temp user folder per test.</summary>
public sealed class RecipeFolderStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ArgonautTests", Guid.NewGuid().ToString("N"));
    private readonly RecipeFolderStore store;

    public RecipeFolderStoreTests()
    {
        store = new RecipeFolderStore(RecipeFolderStore.BundledDirectoryBesideApp, Path.Combine(root, "Recipes"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    [Fact]
    public void EveryPreset_Loads()
    {
        var presets = store.Enumerate();

        Assert.Contains(presets, e => e.DisplayName == "SQL IN list" && !e.IsUser);
        Assert.All(presets, preset => Assert.NotNull(store.Load(preset)));
    }

    [Fact]
    public void SqlPreset_ReadsAsWritten()
    {
        var entry = Assert.Single(store.Enumerate(), e => e.DisplayName == "SQL IN list");

        var recipe = store.Load(entry);

        Assert.Equal(TextEscape.Sql, recipe!.Escape);
        Assert.Equal(",\n", recipe.Separator);
    }

    [Fact]
    public void Save_RoundTrips_AndShadowsAPresetOfTheSameName()
    {
        var mine = new Recipe { Template = "\"{line}\"", Separator = "|", Escape = TextEscape.Csv };

        var saved = store.Save("SQL IN list", mine);

        var entry = Assert.Single(store.Enumerate(), e => e.DisplayName == "SQL IN list");
        Assert.True(entry.IsUser);
        Assert.Equal(saved, entry);
        Assert.Equal(mine, store.Load(entry));
    }

    [Fact]
    public void Save_ReplacesCharactersNoFileSystemAccepts()
    {
        var saved = store.Save("  a/b: c?  ", new Recipe());

        Assert.Equal("a_b_ c_", saved.DisplayName);
        Assert.True(File.Exists(saved.FilePath));
    }

    [Fact]
    public void Save_RefusesAnEmptyName()
    {
        Assert.Throws<ArgumentException>(() => store.Save("  ", new Recipe()));
    }

    [Fact]
    public void Delete_RemovesAUserRecipe_AndLeavesPresetsAlone()
    {
        var saved = store.Save("mine", new Recipe());
        var preset = store.Enumerate().First(e => !e.IsUser);

        store.Delete(saved);
        store.Delete(preset);

        Assert.DoesNotContain(store.Enumerate(), e => e.DisplayName == "mine");
        Assert.True(File.Exists(preset.FilePath));
    }

    [Fact]
    public void Load_ReturnsNull_ForAFileThatDoesNotParse()
    {
        Directory.CreateDirectory(Path.Combine(root, "Recipes"));
        string path = Path.Combine(root, "Recipes", "broken.json");
        File.WriteAllText(path, "{ not json");

        Assert.Null(store.Load(new RecipeEntry("broken", path, IsUser: true)));
    }

    [Fact]
    public void ARecipeFileThatLeavesSettingsOut_GetsTheirDefaults()
    {
        Directory.CreateDirectory(Path.Combine(root, "Recipes"));
        string path = Path.Combine(root, "Recipes", "sparse.json");
        File.WriteAllText(path, """{ "separator": ", " }""");

        var recipe = store.Load(new RecipeEntry("sparse", path, IsUser: true))!;

        Assert.Equal(new Recipe { Separator = ", " }, recipe);
    }

    [Fact]
    public void EveryPreset_HasEverySettingFilledIn()
    {
        foreach (var entry in store.Enumerate())
        {
            var recipe = store.Load(entry)!;
            Assert.NotNull(recipe.Template);
            Assert.NotNull(recipe.Separator);
            Assert.NotNull(recipe.Open);
            Assert.NotNull(recipe.Close);
        }
    }
}
