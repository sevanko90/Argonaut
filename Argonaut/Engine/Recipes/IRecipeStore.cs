using System.Collections.Generic;

namespace Argonaut.Engine.Recipes;

/// <summary>One recipe on offer. <paramref name="DisplayName"/> is the file stem, as a schema's
/// is; <paramref name="IsUser"/> is false for one shipped with the app, which cannot be
/// overwritten or deleted, only shadowed by a user recipe of the same name.</summary>
public readonly record struct RecipeEntry(string DisplayName, string FilePath, bool IsUser);

/// <summary>
/// Where recipes are kept: the presets shipped with the app plus the user's own. The folders
/// are the composition root's to choose, so a sandboxed build can put the user's somewhere it is
/// allowed to write.
/// </summary>
public interface IRecipeStore
{
    /// <summary>Every recipe, sorted by name, a user recipe shadowing a preset of the same name.
    /// Lists folders only - no recipe is read until <see cref="Load"/>.</summary>
    IReadOnlyList<RecipeEntry> Enumerate();

    /// <summary>The recipe, or null when its file has gone or does not parse.</summary>
    Recipe? Load(RecipeEntry entry);

    /// <summary>Saves <paramref name="recipe"/> as the user's recipe <paramref name="displayName"/>,
    /// replacing one of that name. Throws <see cref="System.IO.IOException"/> when it cannot be
    /// written - unlike settings, a save the user asked for must say when it failed.</summary>
    RecipeEntry Save(string displayName, Recipe recipe);

    /// <summary>Deletes a user recipe; a preset is left alone.</summary>
    void Delete(RecipeEntry entry);
}
