using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Argonaut.Engine.Settings;

namespace Argonaut.Features.Json.Schema;

/// <summary>
/// Remembers which JSON Schema was bound to which document, so reopening a file re-applies the
/// schema the user picked last time.
///
/// Stored most-recent-first and capped, because this grows with every document the user ever
/// binds a schema to, unlike the other settings which hold a single value.
/// </summary>
public sealed class SchemaBindings : ISettingsBlock<SchemaBindings>
{
    private const int MaxEntries = 100;

    public static string Key => "schemaBindings";

    public static JsonTypeInfo<SchemaBindings> JsonTypeInfo => SchemaBindingsJson.Default.SchemaBindings;

    /// <summary>
    /// Most recent first. Always replaced whole, never changed in place, so a background reader
    /// holding the previous list keeps a consistent one.
    /// </summary>
    public IReadOnlyList<SchemaBinding> Entries
    {
        get;
        // A hand-edited file can hold null, or more than the cap.
        set => field = value is null ? [] : value.Count > MaxEntries ? value.Take(MaxEntries).ToArray() : value;
    } = [];

    /// <summary>
    /// The schema last bound to this document, or null if there isn't one. The caller still has
    /// to check the schema file exists - it may have been deleted since, and the root may have
    /// been edited out of it.
    /// </summary>
    public static SchemaBinding? Find(IReadOnlyList<SchemaBinding> entries, string documentPath)
    {
        foreach (var entry in entries)
        {
            if (PathsEqual(entry.DocumentPath, documentPath))
                return entry;
        }

        return null;
    }

    /// <summary>Records the schema bound to <paramref name="documentPath"/>, moving it to the
    /// front - or, with a null <paramref name="schemaPath"/>, forgets it.</summary>
    public void Remember(string documentPath, string? schemaPath, string? rootName = null)
    {
        var entries = new List<SchemaBinding>(Math.Min(Entries.Count + 1, MaxEntries));
        if (schemaPath is not null)
            entries.Add(new SchemaBinding(documentPath, schemaPath, rootName));

        foreach (var entry in Entries)
        {
            if (entries.Count >= MaxEntries)
                break;
            if (!PathsEqual(entry.DocumentPath, documentPath))
                entries.Add(entry);
        }

        Entries = entries;
    }

    // Case-insensitive on Windows/macOS-style filesystems, and a false match here only ever
    // pre-selects a schema the user can change - not worth probing the filesystem's real
    // case sensitivity.
    private static bool PathsEqual(string a, string b)
        => string.Equals(a, b, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
}

/// <summary>One document's remembered schema: the schema file, plus which of its named roots was
/// bound (null for the schema's own root).</summary>
public sealed record SchemaBinding(string DocumentPath, string SchemaPath, string? RootName = null);

[JsonSerializable(typeof(SchemaBindings))]
internal sealed partial class SchemaBindingsJson : JsonSerializerContext;
