using System.Collections.Generic;

namespace Argonaut.Infrastructure;

/// <summary>
/// Small bounded LRU map, extracted verbatim from JsonVisibleRowCollection's realized-row
/// cache so the diff row collection (and the child-count cache) can share it. Reads touch;
/// <see cref="Set"/> evicts the least-recently-used entry once <c>capacity</c> is exceeded.
/// Values must be safe to drop at any time - both users cache derivations that can always
/// be recomputed from the index/mapping (a realized row, a container's child count).
///
/// Not thread-safe; UI-thread only, like the collections that own it.
/// </summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int capacity;
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> map = new();
    private readonly LinkedList<(TKey Key, TValue Value)> order = new();

    public LruCache(int capacity)
    {
        this.capacity = capacity;
    }

    public int Count => map.Count;

    public bool TryGetValue(TKey key, out TValue value)
    {
        if (!map.TryGetValue(key, out var node))
        {
            value = default!;
            return false;
        }

        order.Remove(node);
        order.AddFirst(node);
        value = node.Value.Value;
        return true;
    }

    public void Set(TKey key, TValue value)
    {
        if (map.TryGetValue(key, out var existing))
        {
            order.Remove(existing);
            existing.Value = (key, value);
            order.AddFirst(existing);
            return;
        }

        var node = new LinkedListNode<(TKey, TValue)>((key, value));
        order.AddFirst(node);
        map[key] = node;

        if (map.Count > capacity)
        {
            var lru = order.Last!;
            order.RemoveLast();
            map.Remove(lru.Value.Key);
        }
    }

    public void Clear()
    {
        map.Clear();
        order.Clear();
    }
}
