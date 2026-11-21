using System.Collections.Generic;

namespace Argonaut.Features.Hex.Structs;

/// <summary>A saved definition: the name it was saved under, the language its file is in, and
/// the file.</summary>
public sealed record StructDefinitionEntry(string Name, StructLanguage Language, string FilePath);

/// <summary>
/// Where struct definitions are kept once saved: a folder of the user's, one file per
/// definition, as recipes and schemas are kept. A pasted definition is used at once and reaches
/// the store only when saved, so throwaway pastes never fill it. The composition root decides the
/// folder; nothing else opens a file for a definition.
/// </summary>
public interface IStructDefinitionStore
{
    /// <summary>The saved definitions, by name.</summary>
    IReadOnlyList<StructDefinitionEntry> Enumerate();

    /// <summary>The definition's text, or null when its file cannot be read.</summary>
    string? Load(StructDefinitionEntry entry);

    /// <summary>Saves <paramref name="text"/> as <paramref name="name"/>, replacing what was saved
    /// under that name in that language.</summary>
    StructDefinitionEntry Save(string name, StructLanguage language, string text);

    void Delete(StructDefinitionEntry entry);

    /// <summary>Shows the folder in the system's file manager, created if it is not there yet.</summary>
    void RevealFolder();
}
