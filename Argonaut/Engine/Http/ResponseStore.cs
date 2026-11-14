using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Argonaut.Engine.Http;

/// <summary>A kept response as the budget sees it: whose it is, its file, size and age, and
/// whether its request is pinned.</summary>
public sealed record KeptResponse(string EntryId, string FileName, long Bytes, DateTimeOffset FetchedAt, bool Pinned);

/// <summary>
/// The response folder as a store: where a saved request's last response is kept - in the app's
/// own data folder, which the App Store sandbox always allows - within one budget for them all.
/// The folder comes from the composition root. Kept files stay under the name they downloaded as
/// (renaming a file that is open fails on Windows), so keeping one is only a matter of the saved
/// request recording its name.
/// </summary>
public sealed class ResponseStore(string folder)
{
    /// <summary>How much all kept responses may take together.</summary>
    public const long Budget = 1L << 30;

    private const string Extension = ".response";

    /// <summary>The kept file's full path, or null when it is gone - or when the name is not a
    /// plain file name in this folder, as a hand-edited settings file could make it.</summary>
    public string? PathOf(string fileName)
    {
        if (string.IsNullOrEmpty(fileName) || Path.GetFileName(fileName) != fileName || !fileName.EndsWith(Extension, StringComparison.Ordinal))
            return null;

        string path = Path.Combine(folder, fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Deletes a kept file; one in use (a document still has it open, on Windows) is left
    /// for the next sweep.</summary>
    public void Delete(string fileName)
    {
        if (PathOf(fileName) is not { } path)
            return;

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Swept later.
        }
    }

    /// <summary>Deletes every response file no saved request points to - downloads abandoned by a
    /// crash, files a failed delete left behind. Run at startup, before anything downloads.</summary>
    public void Sweep(IReadOnlySet<string> kept)
    {
        if (!Directory.Exists(folder))
            return;

        foreach (string path in Directory.EnumerateFiles(folder, "*" + Extension))
        {
            if (!kept.Contains(Path.GetFileName(path)))
                Delete(Path.GetFileName(path));
        }
    }

    /// <summary>
    /// Which kept responses to let go so the rest fit in <paramref name="budget"/>: the oldest
    /// first, unpinned requests' before pinned ones', never <paramref name="justKept"/> - the
    /// response the user is looking at. Pure, so the order is tested without a disk.
    /// </summary>
    public static IReadOnlyList<KeptResponse> ChooseEvictions(IReadOnlyList<KeptResponse> kept, long budget, string? justKept = null)
    {
        long total = kept.Sum(static k => k.Bytes);
        var evicted = new List<KeptResponse>();
        foreach (var candidate in kept
            .Where(k => k.EntryId != justKept)
            .OrderBy(static k => k.Pinned)
            .ThenBy(static k => k.FetchedAt))
        {
            if (total <= budget)
                break;

            evicted.Add(candidate);
            total -= candidate.Bytes;
        }

        return evicted;
    }
}
