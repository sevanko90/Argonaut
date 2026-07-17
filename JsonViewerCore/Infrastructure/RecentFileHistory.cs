using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace JsonViewerCore.Infrastructure;

public static class RecentFileHistory
{
    private const int MaxEntries = 5;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static string HistoryFilePath
        => Path.Combine(Path.GetTempPath(), "JsonViewerCore", "recent-files.json");

    public static IReadOnlyList<string> Load()
    {
        try
        {
            if (!File.Exists(HistoryFilePath))
                return Array.Empty<string>();

            var json = File.ReadAllText(HistoryFilePath);
            var items = JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? new List<string>();
            return items.Where(static path => !string.IsNullOrWhiteSpace(path)).Take(MaxEntries).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static void Add(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryFilePath)!);

            var normalizedPath = Path.GetFullPath(path);
            var items = Load()
                .Where(existing => !string.Equals(existing, normalizedPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            items.Insert(0, normalizedPath);

            if (items.Count > MaxEntries)
                items = items.Take(MaxEntries).ToList();

            File.WriteAllText(HistoryFilePath, JsonSerializer.Serialize(items, JsonOptions));
        }
        catch
        {
            // History should not block file opening.
        }
    }
}
