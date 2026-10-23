using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Argonaut.Engine.Transforms;

/// <summary>
/// Recipes as one JSON file each, in two folders: the presets shipped beside the app, and the
/// user's own, which is created on the first save. Mirrors the schema catalog, so the two read
/// the same to someone who has used either: name a file, it appears; same name as a preset, it
/// wins.
/// </summary>
/// <param name="bundledDirectory">The presets. Read-only in practice - an install directory is
/// not somewhere the user can save to.</param>
/// <param name="userDirectory">The user's recipes. Must be writable, which in a sandboxed build
/// means inside the container - the composition root's decision, not this class's.</param>
public sealed class RecipeFolderStore(string bundledDirectory, string userDirectory) : IRecipeStore
{
    /// <summary>The presets folder's name beside the executable.</summary>
    public const string BundledFolderName = "Recipes";

    public static string BundledDirectoryBesideApp => Path.Combine(AppContext.BaseDirectory, BundledFolderName);

    public IReadOnlyList<RecipeEntry> Enumerate()
    {
        var byName = new Dictionary<string, RecipeEntry>(StringComparer.OrdinalIgnoreCase);
        AddFolder(byName, bundledDirectory, isUser: false);
        AddFolder(byName, userDirectory, isUser: true);

        var entries = new List<RecipeEntry>(byName.Values);
        entries.Sort(static (a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return entries;
    }

    public Recipe? Load(RecipeEntry entry)
    {
        try
        {
            using var stream = File.OpenRead(entry.FilePath);
            return JsonSerializer.Deserialize(stream, RecipeJson.Default.Recipe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public RecipeEntry Save(string displayName, Recipe recipe)
    {
        string name = FileStem(displayName);
        Directory.CreateDirectory(userDirectory);
        string path = Path.Combine(userDirectory, name + ".json");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(recipe, RecipeJson.Default.Recipe));
        return new RecipeEntry(name, path, IsUser: true);
    }

    public void Delete(RecipeEntry entry)
    {
        if (!entry.IsUser)
            return;

        File.Delete(entry.FilePath);
    }

    /// <summary>The name as a file stem: trimmed, with characters no file system accepts
    /// replaced, so what the user typed is what the picker shows.</summary>
    internal static string FileStem(string displayName)
    {
        string trimmed = displayName.Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("A recipe needs a name.", nameof(displayName));

        // The invalid set differs by platform; refusing the union keeps a recipe folder
        // copyable between machines.
        var stem = trimmed.ToCharArray();
        for (int i = 0; i < stem.Length; i++)
        {
            if (stem[i] < 0x20 || "<>:\"/\\|?*".Contains(stem[i]))
                stem[i] = '_';
        }

        return new string(stem).TrimEnd('.');
    }

    private static void AddFolder(Dictionary<string, RecipeEntry> byName, string directory, bool isUser)
    {
        string[] files;
        try
        {
            files = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json") : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable or vanished folder simply contributes no recipes.
            return;
        }

        foreach (string file in files)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            byName[name] = new RecipeEntry(name, file, isUser);
        }
    }
}
