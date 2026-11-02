using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace PhotoScope.Controls;

/// <summary>ObservableCollection that can replace its contents with a single Reset notification.</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>Removes matching items: individually when there are few (keeps scroll position), else with one Reset.</summary>
    public int RemoveAll(Func<T, bool> predicate)
    {
        var indices = new List<int>();
        for (var i = 0; i < Items.Count; i++)
            if (predicate(Items[i])) indices.Add(i);
        if (indices.Count == 0) return 0;

        if (indices.Count <= 50)
        {
            for (var i = indices.Count - 1; i >= 0; i--) RemoveAt(indices[i]);
            return indices.Count;
        }

        CheckReentrancy();
        for (var i = indices.Count - 1; i >= 0; i--) Items.RemoveAt(indices[i]);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        return indices.Count;
    }
}
