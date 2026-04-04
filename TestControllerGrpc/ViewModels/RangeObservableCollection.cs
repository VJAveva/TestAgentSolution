using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace TestControllerGrpc.ViewModels;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that supports efficient batch
/// operations: <see cref="AddRange"/> and <see cref="TrimFromStart"/>.
///
/// Standard <c>ObservableCollection</c> fires <c>CollectionChanged</c> for
/// every single <c>Add</c>/<c>RemoveAt</c>, causing the bound WPF ListBox
/// to re-layout each time.  At 10K+ log messages this freezes the UI.
///
/// This collection batches items and fires a single <c>Reset</c>
/// notification, reducing layout passes from O(n) to O(1).
///
/// <para><b>Architectural note:</b> This class is WPF-specific
/// (<c>INotifyCollectionChanged</c>) and lives in the ViewModel layer.
/// The WebAPI/WebClient projects consume log data through the
/// <c>IAppLogger</c> ring-buffer in <c>TestControllerGrpc.Core</c>,
/// which is unaffected by this change.</para>
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotification;

    /// <summary>
    /// Adds multiple items and fires a single <see cref="NotifyCollectionChangedAction.Reset"/>
    /// notification instead of one per item.
    /// </summary>
    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var list = items as IList<T> ?? [.. items];
        if (list.Count == 0) return;

        _suppressNotification = true;
        try
        {
            foreach (var item in list)
                Items.Add(item);
        }
        finally
        {
            _suppressNotification = false;
        }

        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>
    /// Removes <paramref name="count"/> items from the beginning of the list
    /// in a single batch, firing one <see cref="NotifyCollectionChangedAction.Reset"/>.
    ///
    /// Replaces the O(n²) pattern:
    /// <code>while (Count &gt; max) RemoveAt(0);</code>
    /// which fires n notifications and shifts items n times.
    /// </summary>
    public void TrimFromStart(int count)
    {
        if (count <= 0) return;
        count = Math.Min(count, Count);

        _suppressNotification = true;
        try
        {
            for (var i = 0; i < count; i++)
                Items.RemoveAt(0);
        }
        finally
        {
            _suppressNotification = false;
        }

        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <inheritdoc/>
    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotification)
            base.OnCollectionChanged(e);
    }
}
