using System.Windows;
using System.Windows.Controls;

namespace TestControllerGrpc.Views.Behaviors;

/// <summary>
/// Attached behavior that scrolls a <see cref="TreeViewItem"/> into view when it
/// becomes selected — including programmatic selection (e.g. after a node is
/// created or a search jumps to a match), not just mouse clicks.
/// </summary>
public static class TreeViewItemBehavior
{
    public static readonly DependencyProperty IsBroughtIntoViewWhenSelectedProperty =
        DependencyProperty.RegisterAttached(
            "IsBroughtIntoViewWhenSelected",
            typeof(bool),
            typeof(TreeViewItemBehavior),
            new UIPropertyMetadata(false, OnIsBroughtIntoViewWhenSelectedChanged));

    public static bool GetIsBroughtIntoViewWhenSelected(TreeViewItem item) =>
        (bool)item.GetValue(IsBroughtIntoViewWhenSelectedProperty);

    public static void SetIsBroughtIntoViewWhenSelected(TreeViewItem item, bool value) =>
        item.SetValue(IsBroughtIntoViewWhenSelectedProperty, value);

    private static void OnIsBroughtIntoViewWhenSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TreeViewItem item) return;

        item.Selected -= OnSelected;
        if (e.NewValue is true)
            item.Selected += OnSelected;
    }

    private static void OnSelected(object sender, RoutedEventArgs e)
    {
        // Ignore selection events bubbling up from a descendant TreeViewItem.
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        (sender as TreeViewItem)?.BringIntoView();
    }
}
