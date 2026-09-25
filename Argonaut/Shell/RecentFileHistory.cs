using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Argonaut.Engine.Settings;

namespace Argonaut.Shell;

/// <summary>The files most recently opened or saved, newest first, offered by the empty state.</summary>
public sealed class RecentFileHistory : ISettingsBlock<RecentFileHistory>
{
    private const int MaxEntries = 5;

    public static string Key => "recentFiles";

    public static JsonTypeInfo<RecentFileHistory> JsonTypeInfo => RecentFileHistoryJson.Default.RecentFileHistory;

    public IReadOnlyList<string> Paths
    {
        get;
        // A hand-edited file can hold null, blanks, or more than the cap.
        set => field = value is null ? [] : value.Where(static path => !string.IsNullOrWhiteSpace(path)).Take(MaxEntries).ToArray();
    } = [];

    /// <summary>Moves <paramref name="path"/> to the front. Ignored if the path cannot be made
    /// absolute - history should never block opening a file.</summary>
    public void Add(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            return;
        }

        Paths = Paths
            .Where(existing => !string.Equals(existing, fullPath, StringComparison.OrdinalIgnoreCase))
            .Prepend(fullPath)
            .ToArray();
    }

    public void Clear() => Paths = [];
}

[JsonSerializable(typeof(RecentFileHistory))]
internal sealed partial class RecentFileHistoryJson : JsonSerializerContext;
