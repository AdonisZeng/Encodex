using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Encodex.Models;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can append many items while raising a
/// single change notification. Filling a large scan result item by item made WPF
/// re-evaluate bindings once per row; a bulk add costs one Reset notification instead.
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotifications;

    public void AddRange(IEnumerable<T> items)
    {
        if (items == null)
            throw new ArgumentNullException(nameof(items));

        _suppressNotifications = true;
        try
        {
            foreach (var item in items)
                Add(item);
        }
        finally
        {
            _suppressNotifications = false;
        }

        // One Reset is enough for listeners to re-read the whole collection.
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void InsertItem(int index, T item)
    {
        // Bypass the base implementation so no per-item event is raised while the
        // bulk append is in flight; the item still lands in the underlying list.
        if (_suppressNotifications)
        {
            Items.Insert(index, item);
            return;
        }

        base.InsertItem(index, item);
    }
}
