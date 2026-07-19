using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Argonaut.Infrastructure;

public static class RecentFileHistory
{
    private const int MaxEntries = 5;
    private const string FileName = "recent-files.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static IReadOnlyList<string> Load()
    {
        var items = JsonSettingsStore.TryLoad<List<string>>(FileName, JsonOptions);
        if (items is null)
            return Array.Empty<string>();

        return items.Where(static path => !string.IsNullOrWhiteSpace(path)).Take(MaxEntries).ToList();
    }

    public static void Clear() => JsonSettingsStore.Delete(FileName);

    public static void Add(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var normalizedPath = Path.GetFullPath(path);
            var items = Load()
                .Where(existing => !string.Equals(existing, normalizedPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            items.Insert(0, normalizedPath);

            if (items.Count > MaxEntries)
                items = items.Take(MaxEntries).ToList();

            JsonSettingsStore.Save(FileName, items, JsonOptions);
        }
        catch
        {
            // History should not block file opening.
        }
    }
}
