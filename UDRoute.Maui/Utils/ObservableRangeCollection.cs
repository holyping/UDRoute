using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace UDRoute.Maui.Utils;

public class ObservableRangeCollection<T> : ObservableCollection<T>
{
    public ObservableRangeCollection() : base() { }

    public ObservableRangeCollection(IEnumerable<T> collection) : base(collection) { }

    /// <summary>
    /// Adds a collection of items, firing a single Reset event instead of one Add event per item.
    /// </summary>
    public void AddRange(IEnumerable<T> collection)
    {
        if (collection == null) throw new ArgumentNullException(nameof(collection));

        var items = (List<T>)Items;
        bool added = false;
        foreach (var i in collection)
        {
            items.Add(i);
            added = true;
        }

        if (added)
        {
            OnPropertyChanged(new PropertyChangedEventArgs("Count"));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    /// <summary>
    /// Clears the collection and adds a new collection of items, firing a single Reset event.
    /// </summary>
    public void ReplaceRange(IEnumerable<T> collection)
    {
        if (collection == null) throw new ArgumentNullException(nameof(collection));

        var items = (List<T>)Items;
        items.Clear();
        foreach (var i in collection)
        {
            items.Add(i);
        }

        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
