using System;
using System.Collections.Generic;

namespace Argonaut.Infrastructure;

/// <summary>
/// Remembers which JSON Schema was bound to which document, so reopening a file re-applies the
/// schema the user picked last time. One small file on top of <see cref="JsonSettingsStore"/>,
/// following <see cref="ExpandDepthPreference"/>'s one-file-per-preference pattern.
///
/// Stored most-recent-first and capped, because this grows with every document the user ever
/// binds a schema to, unlike the other preferences which hold a single value.
/// </summary>
public static class SchemaSelectionPreference
{
    private const string FileName = "schema-selection.json";
    private const int MaxEntries = 100;

    /// <summary>
    /// The schema last bound to this document, or null if there isn't one: the schema file path,
    /// plus which of its named roots was bound (null for the schema's own root - and for every
    /// selection saved before multi-root schemas existed, which is the correct reading of an
    /// absent field). The caller still has to check the schema file exists - it may have been
    /// deleted since, and the root may have been edited out of it.
    /// </summary>
    public static (string SchemaPath, string? RootName)? Load(string documentPath)
    {
        if (string.IsNullOrEmpty(documentPath))
            return null;

        var saved = JsonSettingsStore.TryLoad<SavedSelections>(FileName);
        if (saved?.Entries is null)
            return null;

        foreach (var entry in saved.Entries)
        {
            if (PathsEqual(entry.DocumentPath, documentPath) && entry.SchemaPath is { } path)
                return (path, entry.RootName);
        }

        return null;
    }

    /// <summary>Records (or, with a null <paramref name="schemaPath"/>, forgets) the schema bound
    /// to a document, moving it to the front of the list.</summary>
    public static void Save(string documentPath, string? schemaPath, string? rootName = null)
    {
        if (string.IsNullOrEmpty(documentPath))
            return;

        var existing = JsonSettingsStore.TryLoad<SavedSelections>(FileName)?.Entries ?? Array.Empty<SavedSelection>();

        var entries = new List<SavedSelection>(Math.Min(existing.Count + 1, MaxEntries));
        if (schemaPath is not null)
            entries.Add(new SavedSelection(documentPath, schemaPath, rootName));

        foreach (var entry in existing)
        {
            if (entries.Count >= MaxEntries)
                break;
            if (!PathsEqual(entry.DocumentPath, documentPath))
                entries.Add(entry);
        }

        JsonSettingsStore.Save(FileName, new SavedSelections(entries));
    }

    // Case-insensitive on Windows/macOS-style filesystems, and a false match here only ever
    // pre-selects a schema the user can change - not worth probing the filesystem's real
    // case sensitivity.
    private static bool PathsEqual(string? a, string b)
        => string.Equals(a, b, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private sealed record SavedSelections(IReadOnlyList<SavedSelection> Entries);

    // RootName is optional so a file written by an older build still deserialises - it simply
    // comes back null, meaning "the schema's own root", which is what those builds always meant.
    private sealed record SavedSelection(string DocumentPath, string SchemaPath, string? RootName = null);
}
