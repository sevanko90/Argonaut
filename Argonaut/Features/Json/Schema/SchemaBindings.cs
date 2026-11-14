using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Argonaut.Engine.Settings;

namespace Argonaut.Features.Json.Schema;

/// <summary>
/// Remembers which JSON Schema the user chose for which document, so reopening it re-applies the
/// choice - including "No schema", so a schema that would be matched to the document is not bound
/// again after the user cleared it. Only choices are remembered; a matched schema never is.
///
/// Keyed by the document's <c>SourceLocator.Identity</c>, so a URL document remembers its
/// schema the way a file does, and a document with no locator remembers nothing. Kept in
/// <see cref="SchemaBindingsBlock"/>, sealed by the injected <see cref="ISettingsSealer"/>, because
/// the keys are the paths and URLs the saved sources seal too. Without a sealer (the tests, a
/// view model built on its own) it lives in memory only.
///
/// Stored most-recent-first and capped, because this grows with every document the user ever
/// binds a schema to.
/// </summary>
public sealed class SchemaBindings
{
    private const int MaxEntries = 100;

    private readonly SchemaBindingsBlock? block;
    private readonly ISettingsSealer? sealer;

    /// <summary>A list kept in memory only.</summary>
    public SchemaBindings()
    {
    }

    /// <summary>The list sealed in <paramref name="block"/>; one that will not open starts empty.</summary>
    public SchemaBindings(SchemaBindingsBlock block, ISettingsSealer? sealer)
    {
        this.block = block;
        this.sealer = sealer;

        if (sealer is not null && block.Sealed is { } sealedText && sealer.Open(sealedText) is { } content)
            Entries = Deserialize(content);
    }

    /// <summary>
    /// Most recent first. Always replaced whole, never changed in place, so a background reader
    /// holding the previous list keeps a consistent one.
    /// </summary>
    public IReadOnlyList<SchemaBinding> Entries
    {
        get;
        private set => field = value.Count > MaxEntries ? value.Take(MaxEntries).ToArray() : value;
    } = [];

    /// <summary>
    /// The choice last made for this document, or null if there isn't one. The caller still has
    /// to check the schema file exists - it may have been deleted since, and the root may have
    /// been edited out of it.
    /// </summary>
    public static SchemaBinding? Find(IReadOnlyList<SchemaBinding> entries, string documentKey)
    {
        foreach (var entry in entries)
        {
            if (string.Equals(entry.DocumentKey, documentKey, StringComparison.Ordinal))
                return entry;
        }

        return null;
    }

    /// <summary>Whether the user's last choice for <paramref name="documentKey"/> was
    /// "No schema". False for a document never bound, and for one with no locator.</summary>
    public static bool ChoseNoSchema(IReadOnlyList<SchemaBinding> entries, string? documentKey)
        => documentKey is not null && Find(entries, documentKey) is { SchemaPath: null };

    /// <summary>Records the schema chosen for <paramref name="documentKey"/>, moving it to the
    /// front - with a null <paramref name="schemaPath"/>, that the user chose "No schema".</summary>
    public void Remember(string documentKey, string? schemaPath, string? rootName = null)
    {
        var entries = new List<SchemaBinding>(Math.Min(Entries.Count + 1, MaxEntries))
        {
            new SchemaBinding(documentKey, schemaPath, schemaPath is null ? null : rootName),
        };

        foreach (var entry in Entries)
        {
            if (entries.Count >= MaxEntries)
                break;
            if (!string.Equals(entry.DocumentKey, documentKey, StringComparison.Ordinal))
                entries.Add(entry);
        }

        Entries = entries;

        if (block is not null && sealer?.Seal(JsonSerializer.SerializeToUtf8Bytes(Entries.ToArray(), SchemaBindingsJson.Default.SchemaBindingArray)) is { } sealedText)
            block.Sealed = sealedText;
    }

    private static IReadOnlyList<SchemaBinding> Deserialize(byte[] content)
    {
        try
        {
            return JsonSerializer.Deserialize(content, SchemaBindingsJson.Default.SchemaBindingArray)?
                .Where(static entry => entry is { DocumentKey.Length: > 0 })
                .ToArray() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>One document's remembered choice: the schema file, or null when the user chose
/// "No schema", plus which of its named roots was bound (null for the schema's own root).</summary>
/// <param name="DocumentKey">The document's <c>SourceLocator.Identity</c>.</param>
public sealed record SchemaBinding(string DocumentKey, string? SchemaPath, string? RootName = null);

/// <summary>The settings block the schema bindings live in: one sealed string.</summary>
public sealed class SchemaBindingsBlock : ISettingsBlock<SchemaBindingsBlock>
{
    public static string Key => "documentSchemas";

    public static JsonTypeInfo<SchemaBindingsBlock> JsonTypeInfo => SchemaBindingsJson.Default.SchemaBindingsBlock;

    public string? Sealed { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SchemaBinding[]))]
[JsonSerializable(typeof(SchemaBindingsBlock))]
internal sealed partial class SchemaBindingsJson : JsonSerializerContext;
