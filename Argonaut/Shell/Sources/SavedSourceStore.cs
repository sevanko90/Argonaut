using System;
using System.Collections.Generic;
using System.Linq;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Settings;

namespace Argonaut.Shell.Sources;

/// <summary>
/// The saved sources the empty state offers: what was opened recently, newest first, and what
/// the user pinned, in the order they pinned it. One list behind both - an entry is pinned or not
/// - with the cap on unpinned entries only, so nothing pushes a pin out.
///
/// Kept in <see cref="SavedSourcesBlock"/>, sealed by the injected <see cref="ISettingsSealer"/>
/// and re-sealed on every change, since the settings file is written from the block at shutdown.
/// A block that will not open (another machine, a lost key) is an empty list. With no sealer the
/// list lives in memory only - never written unsealed. UI thread only.
/// </summary>
public sealed class SavedSourceStore
{
    /// <summary>How many unpinned entries are kept.</summary>
    public const int MaxRecent = 5;

    private readonly SavedSourcesBlock block;
    private readonly ISettingsSealer? sealer;

    // Most recent first. Includes entries of kinds this build does not know, which are kept and
    // written back but never listed.
    private readonly List<SavedSource> entries;

    // Pins in the order they were made; ids, since an entry's place in `entries` is recency.
    private readonly List<string> pinOrder;

    public SavedSourceStore(SavedSourcesBlock block, ISettingsSealer? sealer)
    {
        this.block = block;
        this.sealer = sealer;
        entries = Read(block, sealer);
        pinOrder = entries.Where(e => e.IsPinned).Select(e => e.Id).ToList();
    }

    /// <summary>Unpinned entries, most recent first.</summary>
    public IReadOnlyList<SavedSource> Recent => entries.Where(e => !e.IsPinned && e.Locator is not null).ToList();

    /// <summary>Pinned entries, in the order they were pinned.</summary>
    public IReadOnlyList<SavedSource> Pinned
        => pinOrder.Select(Find).Where(e => e is { Locator: not null }).Select(e => e!).ToList();

    /// <summary>
    /// Records that <paramref name="locator"/> was opened: moves its entry to the front, or adds
    /// one named for it. Keeps a name the user gave, and a pin.
    /// </summary>
    public void Touch(SourceLocator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);

        int index = entries.FindIndex(e => locator.IsSamePlace(e.Locator));
        SavedSource entry;
        if (index >= 0)
        {
            entry = entries[index];
            entries.RemoveAt(index);
        }
        else
        {
            var (kind, location) = SavedSource.Describe(locator);
            entry = new SavedSource { Name = locator.DefaultName, Kind = kind, Location = location };
        }

        entries.Insert(0, entry);
        TrimRecent();
        Persist();
    }

    public void TogglePin(string id)
    {
        if (Find(id) is not { } entry)
            return;

        entry.IsPinned = !entry.IsPinned;
        if (entry.IsPinned)
            pinOrder.Add(id);
        else
            pinOrder.Remove(id);

        TrimRecent();
        Persist();
    }

    /// <summary>Renames an entry; a blank name goes back to the locator's own.</summary>
    public void Rename(string id, string name)
    {
        if (Find(id) is not { } entry)
            return;

        entry.Name = string.IsNullOrWhiteSpace(name) ? entry.Locator?.DefaultName ?? entry.Name : name.Trim();
        Persist();
    }

    public void Remove(string id)
    {
        entries.RemoveAll(e => e.Id == id);
        pinOrder.Remove(id);
        Persist();
    }

    /// <summary>Removes every unpinned entry.</summary>
    public void ClearRecent()
    {
        entries.RemoveAll(e => !e.IsPinned);
        Persist();
    }

    public SavedSource? Find(string id) => entries.FirstOrDefault(e => e.Id == id);

    private void TrimRecent()
    {
        int unpinned = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].IsPinned)
                continue;

            if (++unpinned > MaxRecent)
                entries.RemoveAt(i--);
        }
    }

    private void Persist()
    {
        if (sealer is null)
            return;

        // Pins are written in pin order ahead of recency order would lose it, so the list is
        // stored with pins first in that order, then the rest most recent first.
        var ordered = new SavedSourceList
        {
            Entries = [.. pinOrder.Select(Find).Where(e => e is not null).Select(e => e!), .. entries.Where(e => !e.IsPinned)],
        };

        if (sealer.Seal(SavedSourceList.Serialize(ordered)) is { } sealedText)
            block.Sealed = sealedText;
    }

    private static List<SavedSource> Read(SavedSourcesBlock block, ISettingsSealer? sealer)
    {
        if (sealer is null || block.Sealed is not { } sealedText || sealer.Open(sealedText) is not { } content)
            return [];

        return SavedSourceList.Deserialize(content)?.Entries.Where(e => !string.IsNullOrEmpty(e.Id)).ToList() ?? [];
    }
}
