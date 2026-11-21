using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Argonaut.Engine.Settings;

namespace Argonaut.Features.Hex.Structs;

/// <summary>
/// The definitions used lately, saved or not, so a header pasted yesterday is a click away today
/// without having been saved. Most recent first, capped, and kept as their text - sealed in
/// <see cref="RecentStructDefinitionsBlock"/> by the injected <see cref="ISettingsSealer"/>,
/// because a pasted header may be someone's private code. Without a sealer (the tests, a view
/// model built on its own) it lives in memory only.
/// </summary>
public sealed class RecentStructDefinitions
{
    /// <summary>How many are kept.</summary>
    public const int MaxEntries = 8;

    /// <summary>The longest definition remembered; anything longer is surely a whole file of
    /// code, and is worth saving rather than carrying in the settings.</summary>
    public const int MaxLength = 64 * 1024;

    private readonly RecentStructDefinitionsBlock? block;
    private readonly ISettingsSealer? sealer;

    /// <summary>A list kept in memory only.</summary>
    public RecentStructDefinitions()
    {
    }

    /// <summary>The list sealed in <paramref name="block"/>; one that will not open starts empty.</summary>
    public RecentStructDefinitions(RecentStructDefinitionsBlock block, ISettingsSealer? sealer)
    {
        this.block = block;
        this.sealer = sealer;

        if (sealer is not null && block.Sealed is { } sealedText && sealer.Open(sealedText) is { } content)
            Entries = Deserialize(content);
    }

    /// <summary>Most recent first; replaced whole, never changed in place.</summary>
    public IReadOnlyList<string> Entries { get; private set; } = [];

    /// <summary>Puts <paramref name="text"/> first, once; blank text and text over
    /// <see cref="MaxLength"/> are not remembered.</summary>
    public void Remember(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength)
            return;
        if (Entries.Count > 0 && Entries[0] == text)
            return;

        Entries = new[] { text }.Concat(Entries.Where(entry => entry != text)).Take(MaxEntries).ToArray();

        if (this.block is not null && this.sealer?.Seal(JsonSerializer.SerializeToUtf8Bytes(Entries.ToArray(), RecentStructDefinitionsJson.Default.StringArray)) is { } sealedText)
            this.block.Sealed = sealedText;
    }

    private static IReadOnlyList<string> Deserialize(byte[] content)
    {
        try
        {
            return JsonSerializer.Deserialize(content, RecentStructDefinitionsJson.Default.StringArray)?
                .Where(static text => !string.IsNullOrWhiteSpace(text) && text.Length <= MaxLength)
                .Take(MaxEntries)
                .ToArray() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>The settings block the recent definitions live in: one sealed string.</summary>
public sealed class RecentStructDefinitionsBlock : ISettingsBlock<RecentStructDefinitionsBlock>
{
    public static string Key => "recentStructDefinitions";

    public static JsonTypeInfo<RecentStructDefinitionsBlock> JsonTypeInfo => RecentStructDefinitionsJson.Default.RecentStructDefinitionsBlock;

    public string? Sealed { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(RecentStructDefinitionsBlock))]
internal sealed partial class RecentStructDefinitionsJson : JsonSerializerContext;
