using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;

namespace JsonViewerCore.Infrastructure;

public sealed class WindowedLines : IList<string>, INotifyCollectionChanged
{
    private readonly List<string> _items = new();

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public string this[int index] => this._items[index];
    string IList<string>.this[int index] { get => this._items[index]; set => throw new NotSupportedException(); }

    public int Count => this._items.Count;

    public bool IsReadOnly => true;

    public IEnumerator<string> GetEnumerator() => this._items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

    // Rebuild the window in one atomic operation
    public void ReplaceAll(IEnumerable<string> newItems)
    {
        this._items.Clear();
        this._items.AddRange(newItems);

        // Fire ONE reset event
        this.CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    // Unused IList members
    public void Add(string item) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public bool Contains(string item) => this._items.Contains(item);
    public void CopyTo(string[] array, int arrayIndex) => this._items.CopyTo(array, arrayIndex);
    public int IndexOf(string item) => this._items.IndexOf(item);
    public void Insert(int index, string item) => throw new NotSupportedException();
    public bool Remove(string item) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}