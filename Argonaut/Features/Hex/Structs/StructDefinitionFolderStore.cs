using System;
using System.Collections.Generic;
using System.IO;

namespace Argonaut.Features.Hex.Structs;

/// <summary>
/// Saved struct definitions as one text file each in a folder - a C header (<c>.h</c>) or a C#
/// file (<c>.cs</c>), so the folder is as useful outside the app as in it, and a header dropped
/// into it appears in the list. The extension says the language, so a C and a C# definition may
/// share a name.
/// </summary>
/// <param name="directory">The folder; created on the first save. Must be writable, which in a
/// sandboxed build means inside the container - the composition root's decision.</param>
/// <param name="revealDirectory">Shows a folder in the system's file manager.</param>
public sealed class StructDefinitionFolderStore(string directory, Action<string>? revealDirectory = null) : IStructDefinitionStore
{
    public IReadOnlyList<StructDefinitionEntry> Enumerate()
    {
        string[] files;
        try
        {
            files = Directory.Exists(directory) ? Directory.GetFiles(directory) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var entries = new List<StructDefinitionEntry>();
        foreach (string file in files)
        {
            if (LanguageOf(file) is { } language)
                entries.Add(new StructDefinitionEntry(Path.GetFileNameWithoutExtension(file), language, file));
        }

        entries.Sort(static (a, b) =>
        {
            int byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : a.Language.CompareTo(b.Language);
        });
        return entries;
    }

    public string? Load(StructDefinitionEntry entry)
    {
        try
        {
            return File.ReadAllText(entry.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public StructDefinitionEntry Save(string name, StructLanguage language, string text)
    {
        string stem = FileStem(name);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, stem + (language == StructLanguage.CSharp ? ".cs" : ".h"));
        File.WriteAllText(path, text);
        return new StructDefinitionEntry(stem, language, path);
    }

    public void Delete(StructDefinitionEntry entry) => File.Delete(entry.FilePath);

    public void RevealFolder()
    {
        Directory.CreateDirectory(directory);
        revealDirectory?.Invoke(directory);
    }

    /// <summary>The language a file's extension says, or null for a file that is not a definition.</summary>
    private static StructLanguage? LanguageOf(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".h" or ".hpp" or ".hh" or ".c" or ".cpp" => StructLanguage.C,
        ".cs" => StructLanguage.CSharp,
        _ => null,
    };

    /// <summary>The name as a file stem: trimmed, with characters no file system accepts replaced,
    /// so the folder copies between machines.</summary>
    internal static string FileStem(string name)
    {
        string trimmed = name.Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("A definition needs a name.", nameof(name));

        var stem = trimmed.ToCharArray();
        for (int i = 0; i < stem.Length; i++)
        {
            if (stem[i] < 0x20 || "<>:\"/\\|?*".Contains(stem[i]))
                stem[i] = '_';
        }

        return new string(stem).TrimEnd('.');
    }
}
