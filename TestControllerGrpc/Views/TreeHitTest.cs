using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace TestControllerGrpc.Views;

/// <summary>
/// Finds the tree row under the mouse. <c>ItemsControl.ContainerFromElement</c> on a TreeView returns
/// the TOP-LEVEL container, so every right-click selected the root; this walks up to the nearest row.
/// </summary>
internal static class TreeHitTest
{
    /// <summary>The nearest TreeViewItem at or above <paramref name="source"/>, or null for empty space.</summary>
    public static TreeViewItem? ContainerAt(DependencyObject? source)
    {
        for (var d = source; d is not null; d = ParentOf(d))
        {
            if (d is TreeViewItem item) return item;
            if (d is TreeView) return null;
        }
        return null;
    }

    public static bool IsInScrollBar(DependencyObject? source)
    {
        for (var d = source; d is not null; d = ParentOf(d))
        {
            if (d is ScrollBar) return true;
            if (d is TreeViewItem or TreeView) return false;
        }
        return false;
    }

    // A Run inside a TextBlock is a ContentElement with no visual parent.
    private static DependencyObject? ParentOf(DependencyObject d)
        => d is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(d)
            : LogicalTreeHelper.GetParent(d);
}
