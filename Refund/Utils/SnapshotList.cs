using System.Collections;
using System.Collections.ObjectModel;

namespace Refund.Utils;

/// <summary>
/// An ordered collection whose readers enumerate a stable snapshot while writers modify it.
/// Snapshots preserve membership and order, not the state of the referenced objects.
/// Each operation is synchronized; callers must still serialize compound mutations.
/// </summary>
internal sealed class SnapshotList<T> : IReadOnlyCollection<T>
{
    private readonly object _sync = new();
    private readonly List<T> _items = new();

    public int Count
    {
        get
        {
            lock (_sync)
                return _items.Count;
        }
    }

    public ReadOnlyCollection<T> GetSnapshot()
    {
        // The copy and all writes share a lock. Copying an unprotected List with
        // ToList/ToArray would still race with a concurrent add or removal.
        lock (_sync)
            return Array.AsReadOnly(_items.ToArray());
    }

    public void Add(T item)
    {
        lock (_sync)
            _items.Add(item);
    }

    public void Insert(int index, T item)
    {
        lock (_sync)
            _items.Insert(index, item);
    }

    public bool Remove(T item)
    {
        lock (_sync)
            return _items.Remove(item);
    }

    public void RemoveAt(int index)
    {
        lock (_sync)
            _items.RemoveAt(index);
    }

    public void Clear()
    {
        lock (_sync)
            _items.Clear();
    }

    public bool Contains(T item)
    {
        lock (_sync)
            return _items.Contains(item);
    }

    public int IndexOf(T item)
    {
        lock (_sync)
            return _items.IndexOf(item);
    }

    public IEnumerator<T> GetEnumerator() => GetSnapshot().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
