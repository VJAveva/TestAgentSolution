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

    /// <summary>
    /// The row whose header line contains <paramref name="point"/> (relative to <paramref name="tree"/>), at
    /// any X. The indent left of a row is not hit-testable, so a click there must be matched by height.
    /// </summary>
    public static TreeViewItem? RowAt(TreeView tree, Point point)
    {
        foreach (var item in Rows(tree))
        {
            if (item.Template?.FindName("Bd", item) is not FrameworkElement header || header.ActualHeight <= 0) continue;

            var top = header.TranslatePoint(new Point(0, 0), tree).Y;
            if (point.Y >= top && point.Y < top + header.ActualHeight) return item;
        }
        return null;
    }

    /// <summary>Realized, shown rows; children of a collapsed row are skipped even if their containers linger.</summary>
    private static IEnumerable<TreeViewItem> Rows(ItemsControl parent)
    {
        for (var i = 0; i < parent.Items.Count; i++)
        {
            if (parent.ItemContainerGenerator.ContainerFromIndex(i) is not TreeViewItem item) continue;
            if (item.Visibility != Visibility.Visible) continue;
            yield return item;
            if (!item.IsExpanded) continue;
            foreach (var child in Rows(item)) yield return child;
        }
    }

    // A Run inside a TextBlock is a ContentElement with no visual parent.
    private static DependencyObject? ParentOf(DependencyObject d)
        => d is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(d)
            : LogicalTreeHelper.GetParent(d);
}
