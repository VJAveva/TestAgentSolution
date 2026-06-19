using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Highlighting;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.ViewModels;

using TestControllerGrpc.Views.Dialogs;

namespace TestControllerGrpc.Views;

public partial class MainWindow : Window
{
    // ???????????????????????????????????????????????????????????????
    // FEATURE 2B: DRAG-AND-DROP WITHIN EVENTS
    // ???????????????????????????????????????????????????????????????

    private void OnTreePreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(WatchListTreeView);
        _isDragging = false;
    }

    private void OnTreePreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var currentPos = e.GetPosition(WatchListTreeView);
        var diff = currentPos - _dragStartPoint;

        // Minimum drag distance threshold
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        // Get the dragged node
        var treeViewItem = FindTreeViewItemUnderMouse(e);
        if (treeViewItem?.DataContext is not TreeNodeViewModel node) return;

        // Only allow dragging Action, ActionGroup, Ref nodes
        if (node.NodeKind is not ("Action" or "ActionGroup" or "Ref")) return;

        _draggedNode = node;
        _isDragging = true;

        var data = new DataObject("TreeNodeViewModel", node);
        DragDrop.DoDragDrop(WatchListTreeView, data, DragDropEffects.Move);

        _isDragging = false;
        _draggedNode = null;
    }

    private void OnTreeDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;

        if (!e.Data.GetDataPresent("TreeNodeViewModel")) return;
        if (e.Data.GetData("TreeNodeViewModel") is not TreeNodeViewModel draggedNode) return;

        var treeViewItem = FindTreeViewItemAtPoint(e.GetPosition(WatchListTreeView));
        if (treeViewItem?.DataContext is not TreeNodeViewModel targetNode) return;

        // Validate: must be in the same Event subtree
        if (!IsInSameEventSubtree(draggedNode, targetNode)) return;

        // Validate: target must be a valid drop parent or sibling
        if (IsValidDropTarget(draggedNode, targetNode))
            e.Effects = DragDropEffects.Move;

        e.Handled = true;
    }

    private void OnTreeDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("TreeNodeViewModel")) return;
        if (e.Data.GetData("TreeNodeViewModel") is not TreeNodeViewModel draggedNode) return;

        var treeViewItem = FindTreeViewItemAtPoint(e.GetPosition(WatchListTreeView));
        if (treeViewItem?.DataContext is not TreeNodeViewModel targetNode) return;

        if (!IsInSameEventSubtree(draggedNode, targetNode)) return;
        if (!IsValidDropTarget(draggedNode, targetNode)) return;

        // Determine new parent and insert index
        TreeNodeViewModel newParent;
        int insertIndex;

        if (targetNode.NodeKind is "Event" or "ActionGroup")
        {
            // Drop INTO a container � append at end
            newParent = targetNode;
            insertIndex = newParent.Children.Count;
        }
        else
        {
            // Drop NEXT TO a sibling � insert after the target
            newParent = targetNode.Parent!;
            insertIndex = newParent.Children.IndexOf(targetNode) + 1;
        }

        // Don't drop onto self or into own children
        if (ReferenceEquals(draggedNode, targetNode)) return;
        if (IsDescendantOf(targetNode, draggedNode)) return;

        _vm.ReparentNode(draggedNode, newParent, insertIndex);

        // Re-select the moved node
        draggedNode.IsSelected = true;

        e.Handled = true;
    }

    /// <summary>Check if both nodes share the same Event ancestor.</summary>
    private static bool IsInSameEventSubtree(TreeNodeViewModel a, TreeNodeViewModel b)
    {
        var eventA = FindAncestorOfKind(a, "Event");
        var eventB = FindAncestorOfKind(b, "Event");
        return eventA is not null && ReferenceEquals(eventA, eventB);
    }

    private static bool IsValidDropTarget(TreeNodeViewModel dragged, TreeNodeViewModel target)
    {
        // Can drop into Event or ActionGroup containers
        if (target.NodeKind is "Event" or "ActionGroup") return true;

        // Can drop next to a sibling (same parent type as Event/ActionGroup)
        if (target.Parent?.NodeKind is "Event" or "ActionGroup") return true;

        return false;
    }

    private static bool IsDescendantOf(TreeNodeViewModel node, TreeNodeViewModel potentialAncestor)
    {
        var current = node.Parent;
        while (current is not null)
        {
            if (ReferenceEquals(current, potentialAncestor)) return true;
            current = current.Parent;
        }
        return false;
    }

    private static TreeNodeViewModel? FindAncestorOfKind(TreeNodeViewModel node, string kind)
    {
        var current = node;
        while (current is not null)
        {
            if (current.NodeKind == kind) return current;
            current = current.Parent;
        }
        return null;
    }

    private TreeViewItem? FindTreeViewItemUnderMouse(MouseEventArgs e)
    {
        var hitResult = VisualTreeHelper.HitTest(WatchListTreeView, e.GetPosition(WatchListTreeView));
        return FindParent<TreeViewItem>(hitResult?.VisualHit);
    }

    private TreeViewItem? FindTreeViewItemAtPoint(Point point)
    {
        var hitResult = VisualTreeHelper.HitTest(WatchListTreeView, point);
        return FindParent<TreeViewItem>(hitResult?.VisualHit);
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T found) return found;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }
}
