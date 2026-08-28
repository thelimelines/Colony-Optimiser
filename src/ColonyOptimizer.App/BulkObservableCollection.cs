using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace ColonyOptimizer.App;

/// <summary>
/// An observable collection that can replace its contents with one reset notification.
/// This prevents a large saved plan from forcing WPF to relayout after every row.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceWith(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
