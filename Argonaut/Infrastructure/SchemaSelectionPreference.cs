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

    /// <summary>The schema file path last bound to this document, or null if there isn't one.
    /// The caller still has to check the schema file exists - it may have been deleted since.</summary>
    public static string? Load(string documentPath)
    {
        if (string.IsNullOrEmpty(documentPath))
            return null;

        var saved = JsonSettingsStore.TryLoad<SavedSelections>(FileName);
        if (saved?.Entries is null)
            return null;

        foreach (var entry in saved.Entries)
        {
            if (PathsEqual(entry.DocumentPath, documentPath))
                return entry.SchemaPath;
        }

        return null;
    }

    /// <summary>Records (or, with a null <paramref name="schemaPath"/>, forgets) the schema bound
    /// to a document, moving it to the front of the list.</summary>
    public static void Save(string documentPath, string? schemaPath)
    {
        if (string.IsNullOrEmpty(documentPath))
            return;

        var existing = JsonSettingsStore.TryLoad<SavedSelections>(FileName)?.Entries ?? Array.Empty<SavedSelection>();

        var entries = new List<SavedSelection>(Math.Min(existing.Count + 1, MaxEntries));
        if (schemaPath is not null)
            entries.Add(new SavedSelection(documentPath, schemaPath));

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

    private sealed record SavedSelection(string DocumentPath, string SchemaPath);
}
