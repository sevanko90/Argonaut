using System;
using System.Threading;

namespace JsonViewerCore.Infrastructure;

/// <summary>
/// Lock-free, single-writer/multi-reader append-only list of structs, used by the file
/// indexers to publish millions of fixed-size records from the background indexing thread
/// to UI-thread readers. The alternative - a lock around a List&lt;T&gt; - costs an
/// uncontended Enter/Exit pair (~10-20ns) per record on the writer AND per record read on
/// the UI side, which at millions of records is a large fraction of total indexing time.
///
/// Why no lock is needed:
///
///  - Storage is fixed-size segments that are allocated once and never moved or resized.
///    The hazard that forces List&lt;T&gt; users to lock is Add() relocating the backing
///    array while a reader indexes into it; with immovable segments that hazard does not
///    exist.
///
///  - The single cross-thread synchronization point is <see cref="Count"/>. The writer
///    publishes each append with a Volatile.Write of the count AFTER writing the item;
///    readers use a Volatile.Read. That release/acquire pair guarantees a reader that
///    observes Count &gt; i also observes every write that produced item i, including any
///    segment allocation and table growth that made room for it. Both accesses MUST stay
///    volatile: with plain reads/writes the JIT and CPU are free to reorder the count
///    update ahead of the item write (or cache a stale count), and a reader could then
///    index into an unwritten slot.
///
///  - Items below the published Count are immutable, with one escape hatch: the writer may
///    mutate designated fields of an already-published item through <see cref="ItemRef"/>,
///    but any such field MUST be accessed with Volatile.Read/Volatile.Write by BOTH sides
///    (see PackedToken.EndIndex in JsonStructureIndex). A plain read of a concurrently
///    mutated field can be stale or - as part of a wider struct copy - torn.
/// </summary>
public sealed class SegmentedAppendLog<T> where T : struct
{
    private const int SegmentShift = 13;
    private const int SegmentSize = 1 << SegmentShift; // 8192 items per segment
    private const int SegmentMask = SegmentSize - 1;

    // The table is grown by copy-and-swap (never resized in place) so concurrent readers
    // keep a coherent table; the segment arrays it points to never move, so refs handed
    // out by ItemRef stay valid for the lifetime of the log.
    private T[]?[] segments = new T[]?[16];
    private int count;

    /// <summary>
    /// Number of published items. This Volatile.Read is the acquire half of the
    /// release/acquire pair described in the class remarks: readers must observe an index
    /// below Count (via this property or <see cref="ItemRef"/>'s bounds check) before
    /// touching the item, and must not weaken this to a plain read.
    /// </summary>
    public int Count => Volatile.Read(ref count);

    /// <summary>Writer-thread only. Appends one item and returns its index.</summary>
    public int Add(in T item)
    {
        int index = count; // plain read is fine: only the writer thread advances count
        int segmentIndex = index >> SegmentShift;

        var table = segments;
        if (segmentIndex >= table.Length)
        {
            var grown = new T[]?[table.Length * 2];
            Array.Copy(table, grown, table.Length);
            table = grown;
            segments = grown;
        }

        var segment = table[segmentIndex] ??= new T[SegmentSize];
        segment[index & SegmentMask] = item;

        // Release: publishes the item (and any segment/table allocation above) to any
        // reader that subsequently observes the new count. This single volatile store is
        // the entire locking story of this class - do not reorder or weaken it.
        Volatile.Write(ref count, index + 1);
        return index;
    }

    /// <summary>
    /// Direct ref to a published item; <paramref name="index"/> must be below the current
    /// <see cref="Count"/>. Fields are safe to read plainly EXCEPT fields the writer
    /// mutates after publication - those must go through Volatile.Read/Volatile.Write on
    /// both sides (see class remarks).
    /// </summary>
    public ref T ItemRef(int index)
    {
        // The volatile bounds check doubles as the acquire that makes the item readable.
        if ((uint)index >= (uint)Volatile.Read(ref count))
            throw new ArgumentOutOfRangeException(nameof(index));

        return ref segments[index >> SegmentShift]![index & SegmentMask];
    }
}
