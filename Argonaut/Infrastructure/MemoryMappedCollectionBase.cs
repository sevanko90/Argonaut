using System;
using System.Collections;
using System.Collections.Specialized;

namespace Argonaut.Infrastructure;

/// <summary>
/// Shared base for the virtualizing-list ItemsSources that project a memory-mapped file - JSON
/// rows (<c>JsonVisibleRowCollection</c>), NDJSON lines (<c>MemoryMappedFileLineCollection</c>)
/// and CSV rows (<c>CsvRowCollection</c>). It supplies the read-only, fixed-size
/// <see cref="IList"/> + <see cref="INotifyCollectionChanged"/> surface that Avalonia's
/// VirtualizingStackPanel consumes (Count + indexer for realized rows, plus growth
/// notifications), so subclasses only implement <see cref="GetCount"/> and <see cref="GetItem"/>.
///
/// The reason this is a base class and not a copied pattern: it owns the one piece of disposal
/// safety every mmap-backed source must get right. Once disposed, <see cref="Count"/> reports 0
/// and the indexer returns null, so the single trailing walk Avalonia does over the outgoing
/// ItemsSource during a ContentControl content swap reads nothing. Without that, the walk
/// materialises every row of a multi-GB file (a multi-second stall on file close/switch) and
/// dereferences the just-unmapped file - a native use-after-free that crashed with an access
/// violation. Because the base short-circuits <see cref="Count"/>/<see cref="this"/> before ever
/// calling <see cref="GetCount"/>/<see cref="GetItem"/>, a subclass cannot forget it.
/// </summary>
public abstract class MemoryMappedCollectionBase : IList, INotifyCollectionChanged, IDisposable
{
    private bool disposed;

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <summary>
    /// The current live item count. Do NOT special-case disposal here: the base returns 0 once
    /// disposed and never calls this afterwards - just report the real count.
    /// </summary>
    protected abstract int GetCount();

    /// <summary>
    /// Materialises the item at <paramref name="index"/>, reading the mapping as needed. Only
    /// ever invoked while live: the base short-circuits to null once disposed, so this never
    /// runs against an unmapped file.
    /// </summary>
    protected abstract object GetItem(int index);

    /// <summary>
    /// Subclass teardown - stop growth timers, unsubscribe, drop caches. The collection has
    /// already been flipped to its empty, mapping-free state (see <see cref="Dispose"/>) before
    /// this runs.
    /// </summary>
    protected virtual void DisposeCore() { }

    /// <summary>True once <see cref="Dispose"/> has run; exposed for subclass guards if needed.</summary>
    protected bool IsDisposed => disposed;

    /// <summary>Raises <see cref="CollectionChanged"/> - e.g. growth Adds or a rebuild Reset.</summary>
    protected void RaiseCollectionChanged(NotifyCollectionChangedEventArgs e) => CollectionChanged?.Invoke(this, e);

    public int Count => disposed ? 0 : GetCount();

    public object? this[int index]
    {
        get => disposed ? null : GetItem(index);
        set => throw new NotSupportedException();
    }

    public void Dispose()
    {
        if (disposed)
            return;

        // Flip to the empty, mapping-free state BEFORE subclass teardown, so a walk racing this
        // dispose (or one triggered by it) observes Count == 0 rather than reading a dying mapping.
        disposed = true;
        DisposeCore();
    }

    public IEnumerator GetEnumerator()
    {
        int count = Count; // 0 once disposed
        for (int i = 0; i < count; i++)
            yield return this[i];
    }

    // Read-only, fixed-size virtual view: mutation is unsupported, lookups are cheap no-ops.
    public bool IsFixedSize => true;
    public bool IsReadOnly => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public bool Contains(object? value) => false;
    public int IndexOf(object? value) => -1;
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
    public void CopyTo(Array array, int index) => throw new NotSupportedException();
}
