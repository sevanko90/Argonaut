using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Argonaut.Engine.Settings;

namespace Argonaut.Shell;

/// <summary>The files the user has pinned, in the order they pinned them. Unlike
/// <see cref="RecentFileHistory"/> it has no cap and nothing pushes an entry out: a pin stays until
/// the user removes it.</summary>
public sealed class FavouriteFiles : ISettingsBlock<FavouriteFiles>
{
    public static string Key => "favouriteFiles";

    public static JsonTypeInfo<FavouriteFiles> JsonTypeInfo => FavouriteFilesJson.Default.FavouriteFiles;

    public IReadOnlyList<string> Paths
    {
        get;
        // A hand-edited file can hold null, blanks, or the same path twice.
        set => field = value is null
            ? []
            : value.Where(static path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
    } = [];

    public bool Contains(string path) => TryFullPath(path) is { } fullPath && IndexOf(fullPath) >= 0;

    /// <summary>Pins <paramref name="path"/> if it is not pinned, unpins it if it is. Ignored if the
    /// path cannot be made absolute.</summary>
    public void Toggle(string path)
    {
        if (TryFullPath(path) is not { } fullPath)
            return;

        Paths = IndexOf(fullPath) >= 0
            ? Paths.Where(existing => !string.Equals(existing, fullPath, StringComparison.OrdinalIgnoreCase)).ToArray()
            : Paths.Append(fullPath).ToArray();
    }

    private int IndexOf(string fullPath)
    {
        for (var i = 0; i < Paths.Count; i++)
        {
            if (string.Equals(Paths[i], fullPath, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static string? TryFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }
}

[JsonSerializable(typeof(FavouriteFiles))]
internal sealed partial class FavouriteFilesJson : JsonSerializerContext;
