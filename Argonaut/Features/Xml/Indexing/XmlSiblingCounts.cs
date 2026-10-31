using System;
using System.IO.Hashing;
using System.Threading;
using Argonaut.Engine.Collections;

namespace Argonaut.Features.Xml.Indexing;

/// <summary>
/// How many siblings of each name stand before a point in a recorded container, for the
/// <c>k</c> of an XPath step <c>name[k]</c>. That is the k-th child <i>of that name</i>, not the
/// k-th child, so finding it by reading would mean counting from the container's start - up to the
/// whole container, which for a CityModel member near the end of a 500 MB file is the whole file.
/// Instead the scan keeps a small table of name to count for each open element and stores a copy
/// beside each checkpoint (the count of every name before that checkpoint's child), and a copy of
/// the final table when a recorded container closes. A reader then adds at most one checkpoint
/// stride of reading to a stored count.
///
/// A name is its <see cref="KeyOf"/> hash, not its text, so a table is a few machine words however
/// long the names are. Text, comments and processing instructions have reserved keys. A container
/// with more than <see cref="MaxDistinctKeys"/> distinct child keys stores nothing - a table of
/// that many is not small, and such containers are rare - and is counted by reading instead.
///
/// Single writer, lock-free readers, on the append-log rules (see
/// <see cref="SegmentedAppendLog{T}"/>): a snapshot's entries are written before the slot that
/// refers to them is published, and a slot is read with <see cref="Volatile"/>.
/// </summary>
public sealed class XmlSiblingCounts
{
    /// <summary>The most distinct child keys a container may have and still keep its counts.</summary>
    public const int MaxDistinctKeys = 16;

    // A slot is its entries' first index and count packed together; these are the two that are not.
    private const long NoSnapshot = -1;
    private const long NotClosedYet = -2;

    private struct Entry
    {
        public ulong Key;
        public long Count;
    }

    private static readonly ulong TextHash = XxHash3.HashToUInt64("#text"u8);
    private static readonly ulong CommentHash = XxHash3.HashToUInt64("#comment"u8);

    // One slot per checkpoint, in checkpoint order.
    private readonly SegmentedAppendLog<long> beforeCheckpoint = new();

    // One slot per recorded container, in container order; filled in as each closes.
    private readonly SegmentedAppendLog<long> totals = new();

    private readonly SegmentedAppendLog<Entry> entries = new();

    /// <summary>What the first stored snapshot allocates for the entries log; for the guard that
    /// keeps every index log's segment off the large object heap.</summary>
    internal static int EntrySegmentBytes => SegmentedAppendLog<Entry>.SegmentBytes;

    /// <summary>The key of an element or attribute name as written, prefix included.</summary>
    public static ulong KeyOf(ReadOnlySpan<byte> qualifiedName) => XxHash3.HashToUInt64(qualifiedName);

    /// <summary>The key shared by text and CDATA, which an XPath numbers together as
    /// <c>text()</c>.</summary>
    public static ulong TextKey => TextHash;

    public static ulong CommentKey => CommentHash;

    /// <summary>The key of a processing instruction by its target; distinct from an element of the
    /// same name.</summary>
    public static ulong ProcessingInstructionKeyOf(ReadOnlySpan<byte> target) => XxHash3.HashToUInt64(target, seed: 1);

    /// <summary>
    /// How many siblings with <paramref name="key"/> stand before the child that
    /// <paramref name="checkpoint"/> is the start of - zero, and true, for a key not seen yet.
    /// False when no counts were kept for that checkpoint: its container had too many distinct
    /// keys, or the scan has not stored them yet.
    /// </summary>
    public bool TryGetBefore(int checkpoint, ulong key, out long count)
    {
        count = 0;
        if ((uint)checkpoint >= (uint)beforeCheckpoint.Count)
            return false;

        return TryRead(Volatile.Read(ref beforeCheckpoint.ItemRef(checkpoint)), key, out count);
    }

    /// <summary>
    /// How many children of <paramref name="container"/> have <paramref name="key"/>, once the
    /// container has closed. False while it is open, and when it had too many distinct keys.
    /// </summary>
    public bool TryGetTotal(int container, ulong key, out long count)
    {
        count = 0;
        if ((uint)container >= (uint)totals.Count)
            return false;

        return TryRead(Volatile.Read(ref totals.ItemRef(container)), key, out count);
    }

    /// <summary>Stores the counts of the container's children before a checkpoint; the next
    /// checkpoint's slot. <paramref name="tooMany"/> stores that there are none.</summary>
    internal void AddBeforeCheckpoint(ReadOnlySpan<ulong> keys, ReadOnlySpan<long> counts, bool tooMany)
        => beforeCheckpoint.Add(tooMany ? NoSnapshot : Store(keys, counts));

    /// <summary>Stores the counts of a closed recorded container's children.</summary>
    internal void SetTotals(int container, ReadOnlySpan<ulong> keys, ReadOnlySpan<long> counts, bool tooMany)
    {
        while (totals.Count <= container)
            totals.Add(NotClosedYet);

        Volatile.Write(ref totals.ItemRef(container), tooMany ? NoSnapshot : Store(keys, counts));
    }

    private long Store(ReadOnlySpan<ulong> keys, ReadOnlySpan<long> counts)
    {
        int first = entries.Count;
        for (int i = 0; i < keys.Length; i++)
            entries.Add(new Entry { Key = keys[i], Count = counts[i] });

        return ((long)first << 8) | (uint)keys.Length;
    }

    private bool TryRead(long slot, ulong key, out long count)
    {
        count = 0;
        if (slot < 0)
            return false;

        int first = (int)(slot >> 8);
        int length = (int)(slot & 0xFF);
        for (int i = 0; i < length; i++)
        {
            ref var entry = ref entries.ItemRef(first + i);
            if (entry.Key == key)
            {
                count = entry.Count;
                return true;
            }
        }

        return true; // a key that was never seen has none before this point
    }
}
