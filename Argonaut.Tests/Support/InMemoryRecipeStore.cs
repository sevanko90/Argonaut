using Argonaut.Engine.Transforms;

namespace Argonaut.Tests.Support;

/// <summary>An <see cref="IRecipeStore"/> held in a dictionary: presets given up front, user
/// recipes added by <see cref="Save"/>, a user recipe shadowing a preset of the same name.</summary>
internal sealed class InMemoryRecipeStore : IRecipeStore
{
    private readonly Dictionary<string, (RecipeEntry Entry, Recipe Recipe)> presets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (RecipeEntry Entry, Recipe Recipe)> mine = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryRecipeStore(params (string Name, Recipe Recipe)[] presets)
    {
        foreach (var (name, recipe) in presets)
            this.presets[name] = (new RecipeEntry(name, "preset:" + name, IsUser: false), recipe);
    }

    public IReadOnlyList<RecipeEntry> Enumerate()
    {
        var byName = new Dictionary<string, RecipeEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, held) in this.presets)
            byName[name] = held.Entry;
        foreach (var (name, held) in this.mine)
            byName[name] = held.Entry;

        return byName.Values.OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Recipe? Load(RecipeEntry entry) =>
        (entry.IsUser ? this.mine : this.presets).TryGetValue(entry.DisplayName, out var held) ? held.Recipe : null;

    public RecipeEntry Save(string displayName, Recipe recipe)
    {
        string name = displayName.Trim();
        if (name.Length == 0)
            throw new ArgumentException("A recipe needs a name.", nameof(displayName));

        var entry = new RecipeEntry(name, "mine:" + name, IsUser: true);
        this.mine[name] = (entry, recipe);
        return entry;
    }

    public void Delete(RecipeEntry entry)
    {
        if (entry.IsUser)
            this.mine.Remove(entry.DisplayName);
    }
}
