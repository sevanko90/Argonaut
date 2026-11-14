using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Settings;

namespace Argonaut.Shell.Sources;

/// <summary>
/// One saved source - a place a document can be fetched again from, as the user named it.
/// Settable throughout, as everything read from a file a later build may add fields to must be
/// (see CLAUDE.md).
/// </summary>
public sealed class SavedSource
{
    /// <summary>Stable across renames and moves, so a later hierarchy can refer to an entry.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>What the row shows: the locator's default name until the user renames it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Which kind of locator <see cref="Location"/> is - see <see cref="FileKind"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The locator's own text: a path, a URL.</summary>
    public string Location { get; set; } = string.Empty;

    public bool IsPinned { get; set; }

    /// <summary>A file's bookmark, base64 - what finds it again on macOS - or null.</summary>
    public string? Access { get; set; }

    /// <summary>A file's kind.</summary>
    public const string FileKind = "file";

    /// <summary>The locator this entry names, or null for a kind this build does not know - kept,
    /// so a newer build's entries survive a round trip through an older one, but not listed.</summary>
    [JsonIgnore]
    public SourceLocator? Locator => Kind switch
    {
        FileKind when !string.IsNullOrWhiteSpace(Location) => new FileLocator(Location, Bookmark()),
        _ => null,
    };

    /// <summary>Points this entry at <paramref name="locator"/> - its place and its access.</summary>
    public void Locate(SourceLocator locator)
    {
        switch (locator)
        {
            case FileLocator file:
                Kind = FileKind;
                Location = file.Path;
                Access = file.Bookmark is { } bookmark ? Convert.ToBase64String(bookmark) : null;
                break;
            default:
                throw new ArgumentException($"No saved form for {locator.GetType().Name}.", nameof(locator));
        }
    }

    private byte[]? Bookmark()
    {
        try
        {
            return Access is { Length: > 0 } text ? Convert.FromBase64String(text) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>The saved sources as sealed: most recent first, pinned and unpinned together.</summary>
public sealed class SavedSourceList
{
    public List<SavedSource> Entries { get; set; } = [];

    public static byte[] Serialize(SavedSourceList list) => JsonSerializer.SerializeToUtf8Bytes(list, SavedSourceJson.Default.SavedSourceList);

    /// <summary>The list in <paramref name="utf8"/>, or null when it is not one.</summary>
    public static SavedSourceList? Deserialize(byte[] utf8)
    {
        try
        {
            return JsonSerializer.Deserialize(utf8, SavedSourceJson.Default.SavedSourceList);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The settings block the saved sources live in: one sealed string, so neither a path nor
/// a URL - which can carry an API key - is readable from <c>settings.json</c>.</summary>
public sealed class SavedSourcesBlock : ISettingsBlock<SavedSourcesBlock>
{
    public static string Key => "savedSources";

    public static System.Text.Json.Serialization.Metadata.JsonTypeInfo<SavedSourcesBlock> JsonTypeInfo => SavedSourceJson.Default.SavedSourcesBlock;

    /// <summary>The sealed <see cref="SavedSourceList"/>, or null before anything was saved.</summary>
    public string? Sealed { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SavedSourceList))]
[JsonSerializable(typeof(SavedSourcesBlock))]
internal sealed partial class SavedSourceJson : JsonSerializerContext;
