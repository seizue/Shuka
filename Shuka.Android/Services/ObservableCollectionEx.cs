using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Shuka.Android.Services;

/// <summary>
/// An ObservableCollection that supports suppressing notifications during bulk operations.
/// </summary>
public class ObservableCollectionEx<T> : ObservableCollection<T>
{
    private bool _suppressNotification = false;

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotification)
            base.OnCollectionChanged(e);
    }

    /// <summary>
    /// Adds a range of items to the collection and fires a single Reset notification at the end.
    /// </summary>
    public void AddRange(IEnumerable<T> items)
    {
        if (items == null) return;

        _suppressNotification = true;
        try
        {
            foreach (var item in items)
            {
                Add(item);
            }
        }
        finally
        {
            _suppressNotification = false;
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
