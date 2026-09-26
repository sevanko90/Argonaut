using System.Collections.Generic;

namespace Argonaut.Engine.Indexing.Trees;

/// <summary>
/// Which containers of a tree are expanded: a default - every container shallower than
/// <see cref="DefaultDepth"/> - plus the containers the user has toggled away from it, keyed by the
/// container's first byte. Policy plus overrides rather than a set of expanded containers, so
/// changing the default touches nothing, and keyed by offset rather than any index so a key is
/// stable while the document is still being scanned.
/// </summary>
public sealed class TreeExpandState
{
    private readonly HashSet<long> overrides = new();

    public TreeExpandState(int defaultDepth) => DefaultDepth = defaultDepth;

    /// <summary>Containers at a depth below this are expanded unless overridden; 0 collapses
    /// everything.</summary>
    public int DefaultDepth { get; set; }

    public bool IsExpanded(long containerStart, int depth) => (depth < DefaultDepth) ^ overrides.Contains(containerStart);

    public void Toggle(long containerStart)
    {
        if (!overrides.Remove(containerStart))
            overrides.Add(containerStart);
    }

    public void SetExpanded(long containerStart, int depth, bool expanded)
    {
        if (IsExpanded(containerStart, depth) != expanded)
            Toggle(containerStart);
    }

    /// <summary>Drops the overrides of every container starting strictly between
    /// <paramref name="start"/> and <paramref name="end"/> - a container's descendants, given its
    /// own start and end - so re-expanding it shows the default beneath it again.</summary>
    public void ResetWithin(long start, long end) => overrides.RemoveWhere(o => o > start && o < end);

    /// <summary>Drops every override, leaving only the default.</summary>
    public void Reset() => overrides.Clear();
}
