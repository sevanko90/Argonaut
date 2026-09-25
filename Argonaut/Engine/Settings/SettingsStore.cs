using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Argonaut.Engine.Settings;

/// <summary>
/// The one settings file: a JSON object with a property per <see cref="ISettingsBlock{TSelf}"/>
/// key. Read once at startup, and written once by <see cref="Save"/> at shutdown - nothing
/// watches the blocks, so a setter never touches the disk and there is never more than one
/// writer.
///
/// Every block is held as its serialised <see cref="JsonElement"/> until something asks for it
/// by type. That is what keeps this class free of feature types, and it means a key nobody asks
/// for in this run - a block from a newer build, or a feature not opened yet - is written back
/// untouched rather than dropped.
///
/// Persistence is best-effort by design: a missing or unreadable file yields defaults, and a
/// failed write is swallowed, because settings must never block opening a file or switching
/// theme. The price of writing only at shutdown is that a crash loses that session's changes.
///
/// UI-thread only, like the blocks it hands out.
/// </summary>
public sealed class SettingsStore : ISettingsStore
{
    private readonly string? filePath;
    private readonly Dictionary<string, JsonElement> stored;

    // Every block handed out, keyed by its Key, each with how to serialise it.
    private readonly Dictionary<string, (object Block, Func<JsonElement> Serialize)> blocks = new(StringComparer.Ordinal);

    private SettingsStore(string? filePath, Dictionary<string, JsonElement> stored)
    {
        this.filePath = filePath;
        this.stored = stored;
    }

    /// <summary>A store backed by the file at <paramref name="filePath"/>, which need not exist
    /// yet. Reads it synchronously - it is small, and read once at startup.</summary>
    public static SettingsStore Open(string filePath) => new(filePath, ReadFile(filePath));

    /// <summary>A store that never touches the disk, starting from defaults.</summary>
    public static SettingsStore InMemory() => new(null, new Dictionary<string, JsonElement>(StringComparer.Ordinal));

    public T Get<T>() where T : class, ISettingsBlock<T>, new()
    {
        if (blocks.TryGetValue(T.Key, out var existing))
        {
            return existing.Block as T
                ?? throw new InvalidOperationException(
                    $"Settings key '{T.Key}' is claimed by both {existing.Block.GetType().Name} and {typeof(T).Name}.");
        }

        var block = Read<T>();
        blocks.Add(T.Key, (block, () => JsonSerializer.SerializeToElement(block, T.JsonTypeInfo)));
        return block;
    }

    public void Save()
    {
        if (filePath is null)
            return;

        try
        {
            foreach (var (key, (_, serialize)) in blocks)
                stored[key] = serialize();

            WriteFile(filePath, stored);
        }
        catch
        {
            // Best-effort - see class remarks.
        }
    }

    private T Read<T>() where T : class, ISettingsBlock<T>, new()
    {
        if (!stored.TryGetValue(T.Key, out var element))
            return new T();

        try
        {
            return element.Deserialize(T.JsonTypeInfo) ?? new T();
        }
        catch
        {
            return new T();
        }
    }

    private static Dictionary<string, JsonElement> ReadFile(string path)
    {
        var stored = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path))
                return stored;

            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return stored;

            foreach (var property in document.RootElement.EnumerateObject())
                stored[property.Name] = property.Value.Clone();
        }
        catch
        {
            // Best-effort - see class remarks. A corrupt file starts over from defaults.
            stored.Clear();
        }

        return stored;
    }

    // Written beside the file and moved over it, so a crash mid-write leaves the old settings
    // rather than half of the new ones.
    private static void WriteFile(string path, Dictionary<string, JsonElement> stored)
    {
        string staging = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = File.Create(staging))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var (key, element) in stored.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(key);
                element.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        File.Move(staging, path, overwrite: true);
    }
}
