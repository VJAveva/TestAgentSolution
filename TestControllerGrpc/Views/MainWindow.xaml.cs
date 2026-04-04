using System.Collections.Specialized;
using System.ComponentModel;
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
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    // ?? Inline AvalonEdit editor ?????????????????????????????????????
    private TextEditor? _inlineEditor;
    private FoldingManager? _inlineFoldingManager;
    private XmlFoldingStrategy? _inlineFoldingStrategy;

    // ?? Drag-and-drop state ??????????????????????????????????????????
    private Point _dragStartPoint;
    private TreeNodeViewModel? _draggedNode;
    private bool _isDragging;

    // ?? Dockable pane saved sizes ????????????????????????????????????
    private GridLength _savedAgentColWidth = new(3, GridUnitType.Star);
    private GridLength _savedLogRowHeight = new(2, GridUnitType.Star);

    public MainWindow()
    {
        InitializeComponent();
        _vm = App.Services.GetRequiredService<MainViewModel>();
        DataContext = _vm;

        Loaded += OnWindowLoaded;

        WatchListTreeView.SelectedItemChanged += OnWatchListSelectionChanged;
        TemplateTreeView.SelectedItemChanged += OnTemplateSelectionChanged;

        SubscribeToLogAutoScroll();

        _vm.ScrollToLogEntry += OnScrollToLogEntry;

        // ?? Context menus (built in code-behind) ????????????????????
        WatchListTreeView.ContextMenuOpening += OnWatchListContextMenuOpening;
        TemplateTreeView.ContextMenuOpening += OnTemplateContextMenuOpening;

        // ?? Drag-and-drop handlers ??????????????????????????????????
        WatchListTreeView.PreviewMouseLeftButtonDown += OnTreePreviewMouseDown;
        WatchListTreeView.PreviewMouseMove += OnTreePreviewMouseMove;
        WatchListTreeView.DragOver += OnTreeDragOver;
        WatchListTreeView.Drop += OnTreeDrop;

        // ?? Inline AvalonEdit setup ?????????????????????????????????
        SetupInlineXmlEditor();
        _vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    // ???????????????????????????????????????????????????????????????
    // WINDOW LIFECYCLE
    // ???????????????????????????????????????????????????????????????

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _vm.SyncRegisteredAgents();
            _vm.EnsureWatchListSelected();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnWatchListSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeNodeViewModel node)
            _vm.SelectedNode = node;
    }

    private void OnTemplateSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeNodeViewModel node)
            _vm.SelectedTemplateNode = node;
    }

    // ???????????????????????????????????????????????????????????????
    // FEATURE 1: INLINE AVLONEDIT XML EDITOR
    // ???????????????????????????????????????????????????????????????

    private void SetupInlineXmlEditor()
    {
        _inlineEditor = new TextEditor
        {
            SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("XML"),
            ShowLineNumbers = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            WordWrap = false,
            Background = Brushes.Transparent,
            Foreground = (Brush)FindResource("TextP"),
            LineNumbersForeground = (Brush)FindResource("TextS"),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _inlineEditor.Options.EnableHyperlinks = false;
        _inlineEditor.Options.ConvertTabsToSpaces = true;
        _inlineEditor.Options.IndentationSize = 2;
        _inlineEditor.Options.HighlightCurrentLine = true;

        _inlineEditor.TextChanged += (_, _) =>
        {
            if (_vm.InlineXmlEditorText != _inlineEditor.Text)
                _vm.InlineXmlEditorText = _inlineEditor.Text;
            UpdateInlineFolding();
        };

        InlineXmlEditorHost.Child = _inlineEditor;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsInlineXmlEditorVisible))
        {
            if (_vm.IsInlineXmlEditorVisible && _inlineEditor is not null)
            {
                _inlineEditor.Text = _vm.InlineXmlEditorText;
                SetupInlineFolding();
                Dispatcher.InvokeAsync(() => _inlineEditor.Focus(),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.InlineXmlEditorText))
        {
            if (_inlineEditor is not null && _inlineEditor.Text != _vm.InlineXmlEditorText)
                _inlineEditor.Text = _vm.InlineXmlEditorText;
        }
        else if (e.PropertyName == nameof(MainViewModel.IsAgentPanePinned))
        {
            ApplyAgentPaneLayout(_vm.IsAgentPanePinned);
        }
        else if (e.PropertyName == nameof(MainViewModel.IsLogPanePinned))
        {
            ApplyLogPaneLayout(_vm.IsLogPanePinned);
        }
    }

    private void ApplyAgentPaneLayout(bool pinned)
    {
        if (pinned)
        {
            ColAgentPanel.Width = _savedAgentColWidth;
            ColAgentPanel.MinWidth = 280;
            ColAgentSplitter.Width = GridLength.Auto;
            ColNodeProperties.Width = new GridLength(3, GridUnitType.Star);
        }
        else
        {
            if (ColAgentPanel.Width.IsStar)
                _savedAgentColWidth = ColAgentPanel.Width;

            ColAgentPanel.Width = GridLength.Auto;
            ColAgentPanel.MinWidth = 28;
            ColAgentSplitter.Width = new GridLength(0);
            ColNodeProperties.Width = new GridLength(1, GridUnitType.Star);
        }
    }

    private void ApplyLogPaneLayout(bool pinned)
    {
        if (pinned)
        {
            RowLogPane.Height = _savedLogRowHeight;
            RowLogPane.MinHeight = 120;
        }
        else
        {
            if (RowLogPane.Height.IsStar)
                _savedLogRowHeight = RowLogPane.Height;

            RowLogPane.Height = GridLength.Auto;
            RowLogPane.MinHeight = 28;
        }
    }

    private void SetupInlineFolding()
    {
        if (_inlineEditor is null) return;
        if (_inlineFoldingManager is not null)
        {
            FoldingManager.Uninstall(_inlineFoldingManager);
            _inlineFoldingManager = null;
        }
        _inlineFoldingManager = FoldingManager.Install(_inlineEditor.TextArea);
        _inlineFoldingStrategy = new XmlFoldingStrategy();
        UpdateInlineFolding();
    }

    private void UpdateInlineFolding()
    {
        if (_inlineFoldingManager is not null && _inlineFoldingStrategy is not null && _inlineEditor is not null)
        {
            try { _inlineFoldingStrategy.UpdateFoldings(_inlineFoldingManager, _inlineEditor.Document); }
            catch (Exception) { /* XML parse errors expected during mid-edit — folding will retry on next keystroke */ }
        }
    }

    // ???????????????????????????????????????????????????????????????
    // FEATURE 2A: CONTEXT MENUS (built in code-behind)
    // ???????????????????????????????????????????????????????????????

    // ?? Context menu caches (rebuilt only when node kind changes) ???
    private string? _lastWatchListContextMenuNodeKind;
    private ContextMenu? _cachedWatchListContextMenu;
    private string? _lastTemplateContextMenuNodeKind;
    private ContextMenu? _cachedTemplateContextMenu;

    private void OnWatchListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var node = WatchListTreeView.SelectedItem as TreeNodeViewModel;
        var nodeKind = node?.NodeKind;
        if (nodeKind != _lastWatchListContextMenuNodeKind || _cachedWatchListContextMenu is null)
        {
            _cachedWatchListContextMenu = BuildWatchListContextMenu(node);
            _lastWatchListContextMenuNodeKind = nodeKind;
        }
        WatchListTreeView.ContextMenu = _cachedWatchListContextMenu;
    }

    private ContextMenu BuildWatchListContextMenu(TreeNodeViewModel? node)
    {
        var menu = new ContextMenu
        {
            Background = (Brush)FindResource("BgCard"),
            Foreground = (Brush)FindResource("TextP"),
            BorderBrush = (Brush)FindResource("Bdr"),
        };

        if (node is null) return menu;

        // ?? Execution commands ??????????????????????????????????????
        if (node.NodeKind is "WatchItem")
        {
            menu.Items.Add(CreateMenuItem("? Trigger All Events", _vm.TriggerWatchItemCommand));
            menu.Items.Add(new Separator());
        }
        if (node.NodeKind is "Event")
        {
            menu.Items.Add(CreateMenuItem("? Trigger Event", _vm.TriggerEventCommand));
            menu.Items.Add(new Separator());
        }
        if (node.NodeKind is "ActionGroup")
        {
            menu.Items.Add(CreateMenuItem("? Execute Group", _vm.ExecuteGroupCommand));
            menu.Items.Add(new Separator());
        }
        if (node.NodeKind is "Action")
        {
            menu.Items.Add(CreateMenuItem("? Execute Action", _vm.ExecuteSingleActionCommand));
            menu.Items.Add(new Separator());
        }

        // ?? Add commands (context-sensitive) ??????????????????????????
        if (node.NodeKind is "WatchList")
        {
            menu.Items.Add(CreateMenuItem("Add WatchItem", _vm.AddWatchItemCommand));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem("Import WatchItems…", _vm.ImportWatchItemsCommand));
            menu.Items.Add(CreateMenuItem("Export All WatchItems…", _vm.ExportWatchItemsCommand));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem("? Edit WatchList XML…", _vm.OpenWatchListEditorCommand));
        }
        else if (node.NodeKind is "WatchItem")
        {
            menu.Items.Add(CreateMenuItem("Add Event", _vm.AddChildNodeCommand));
            menu.Items.Add(CreateMenuItem("Add ActionGroup", _vm.AddActionGroupCommand));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem("Export WatchItem…", _vm.ExportWatchItemsCommand));
        }
        else if (node.NodeKind is "Event" or "ActionGroup")
        {
            menu.Items.Add(CreateMenuItem("Add ActionGroup", _vm.AddActionGroupCommand));
            menu.Items.Add(CreateMenuItem("Add Action", _vm.AddActionToGroupCommand));
            menu.Items.Add(CreateMenuItem("Add Ref", _vm.AddRefToGroupCommand));
            menu.Items.Add(CreateMenuItem("Add Initialize", _vm.AddInitializeToGroupCommand));
        }

        // ?? Change ExecutionType submenu (Event / ActionGroup) ??????
        if (node.NodeKind is "Event" or "ActionGroup")
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(BuildExecutionTypeSubmenu(node));
        }

        // ?? Delete ??????????????????????????????????????????????????
        if (node.NodeKind is not "WatchList")
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem("Delete", _vm.ConfirmDeleteSelectedNodeCommand));
        }

        // ?? Retry Failed (WatchItem/Event with previous failed session) ??
        if (node.NodeKind is "WatchItem" or "Event")
        {
            var retryTag = node.NodeKind == "WatchItem" ? node.Tag : node.Parent?.Tag;
            if (!string.IsNullOrEmpty(retryTag))
            {
                var sessionMgr = App.Services.GetRequiredService<ExecutionSessionManager>();
                var lastSession = sessionMgr.GetLastSession(retryTag);
                if (lastSession?.FailedCount > 0)
                {
                    menu.Items.Add(new Separator());
                    var retryItem = new MenuItem
                    {
                        Header = $"? Retry Failed ({lastSession.FailedCount} action{(lastSession.FailedCount > 1 ? "s" : "")})",
                        Command = _vm.RetryFailedCommand
                    };
                    menu.Items.Add(retryItem);
                }
            }
        }

        // ?? Move Up / Down ??????????????????????????????????????????
        if (CanShowMoveItems(node))
        {
            menu.Items.Add(new Separator());

            if (MainViewModel.CanMoveNode(node, -1))
            {
                var moveUp = new MenuItem { Header = "Move Up" };
                var capturedNode = node;
                moveUp.Click += (_, _) => _vm.MoveNodeUp(capturedNode);
                menu.Items.Add(moveUp);
            }

            if (MainViewModel.CanMoveNode(node, +1))
            {
                var moveDown = new MenuItem { Header = "Move Down" };
                var capturedNode = node;
                moveDown.Click += (_, _) => _vm.MoveNodeDown(capturedNode);
                menu.Items.Add(moveDown);
            }
        }

        return menu;
    }

    private void OnTemplateContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var node = TemplateTreeView.SelectedItem as TreeNodeViewModel;
        var nodeKind = node?.NodeKind;
        if (nodeKind != _lastTemplateContextMenuNodeKind || _cachedTemplateContextMenu is null)
        {
            _cachedTemplateContextMenu = BuildTemplateContextMenu(node);
            _lastTemplateContextMenuNodeKind = nodeKind;
        }
        TemplateTreeView.ContextMenu = _cachedTemplateContextMenu;
    }

    private ContextMenu BuildTemplateContextMenu(TreeNodeViewModel? node)
    {
        var menu = new ContextMenu
        {
            Background = (Brush)FindResource("BgCard"),
            Foreground = (Brush)FindResource("TextP"),
            BorderBrush = (Brush)FindResource("Bdr"),
        };

        if (node is null) return menu;

        if (node.NodeKind is "TemplateList")
        {
            menu.Items.Add(CreateMenuItem("Add Template", _vm.AddTemplateCommand));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem("Edit Template XML", _vm.EditTemplateXmlCommand));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem("Import Templates…", _vm.ImportTemplatesCommand));
            menu.Items.Add(CreateMenuItem("Export All Templates…", _vm.ExportTemplatesCommand));
        }
        else if (node.NodeKind is "Template")
        {
            menu.Items.Add(CreateMenuItem("Add ActionGroup", _vm.AddGroupToTemplateCommand));
            menu.Items.Add(CreateMenuItem("Add Action", _vm.AddActionToTemplateCommand));
            menu.Items.Add(CreateMenuItem("Add Ref", _vm.AddRefToTemplateCommand));
            menu.Items.Add(CreateMenuItem("Add Initialize", _vm.AddInitializeToTemplateCommand));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem("Export Template…", _vm.ExportTemplatesCommand));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem("Delete", _vm.DeleteTemplateCommand));
        }
        else if (node.NodeKind is "ActionGroup")
        {
            menu.Items.Add(CreateMenuItem("Add Action", _vm.AddActionToTemplateCommand));
            menu.Items.Add(CreateMenuItem("Add ActionGroup", _vm.AddGroupToTemplateCommand));
            menu.Items.Add(CreateMenuItem("Add Ref", _vm.AddRefToTemplateCommand));
            menu.Items.Add(new Separator());
            menu.Items.Add(BuildExecutionTypeSubmenu(node));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem("Delete", _vm.DeleteTemplateCommand));
        }
        else if (node.NodeKind is not "TemplateList")
        {
            menu.Items.Add(CreateMenuItem("Delete", _vm.DeleteTemplateCommand));
        }

        // ?? Move Up / Down for templates ????????????????????????????
        if (CanShowMoveItems(node))
        {
            menu.Items.Add(new Separator());

            if (MainViewModel.CanMoveNode(node, -1))
            {
                var moveUp = new MenuItem { Header = "Move Up" };
                var capturedNode = node;
                moveUp.Click += (_, _) => _vm.MoveNodeUp(capturedNode);
                menu.Items.Add(moveUp);
            }

            if (MainViewModel.CanMoveNode(node, +1))
            {
                var moveDown = new MenuItem { Header = "Move Down" };
                var capturedNode = node;
                moveDown.Click += (_, _) => _vm.MoveNodeDown(capturedNode);
                menu.Items.Add(moveDown);
            }
        }

        return menu;
    }

    private static bool CanShowMoveItems(TreeNodeViewModel node)
    {
        // Initialize is always first — no move
        if (node.NodeKind is "WatchList" or "TemplateList" or "Initialize") return false;
        return MainViewModel.CanMoveNode(node, -1) || MainViewModel.CanMoveNode(node, +1);
    }

    private static MenuItem CreateMenuItem(string header, ICommand command)
    {
        return new MenuItem { Header = header, Command = command };
    }

    /// <summary>Builds a "Change Execution Type" submenu with Sequential/Parallel options.</summary>
    private MenuItem BuildExecutionTypeSubmenu(TreeNodeViewModel node)
    {
        var submenu = new MenuItem { Header = "Change Execution Type" };

        var isSequential = string.Equals(node.ExecutionTypeText, "Sequential", StringComparison.OrdinalIgnoreCase);
        var isParallel = string.Equals(node.ExecutionTypeText, "Parallel", StringComparison.OrdinalIgnoreCase);

        var seqItem = new MenuItem
        {
            Header = "Sequential",
            IsCheckable = true,
            IsChecked = isSequential
        };
        var capturedNode1 = node;
        seqItem.Click += (_, _) => _vm.ChangeExecutionType(capturedNode1, Models.ExecutionMode.Sequential);

        var parItem = new MenuItem
        {
            Header = "Parallel",
            IsCheckable = true,
            IsChecked = isParallel
        };
        var capturedNode2 = node;
        parItem.Click += (_, _) => _vm.ChangeExecutionType(capturedNode2, Models.ExecutionMode.Parallel);

        submenu.Items.Add(seqItem);
        submenu.Items.Add(parItem);

        return submenu;
    }

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
            // Drop INTO a container — append at end
            newParent = targetNode;
            insertIndex = newParent.Children.Count;
        }
        else
        {
            // Drop NEXT TO a sibling — insert after the target
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

    // ???????????????????????????????????????????????????????????????????
    // LOG AUTO-SCROLL (GAP 1+4 fix)
    // Now subscribes to LogBufferService.BatchFlushed instead of per-item
    // CollectionChanged. One scroll per batch (every ~100ms) vs per item.
    // ???????????????????????????????????????????????????????????????????

    private void SubscribeToLogAutoScroll()
    {
        if (_vm.LogBuffer is not null)
        {
            _vm.LogBuffer.BatchFlushed += OnLogBatchFlushed;
        }
    }

    private void OnLogBatchFlushed(int count)
    {
        if (!_vm.IsAutoScrollEnabled || count == 0)
            return;

        // Already on UI thread (DispatcherTimer fires on dispatcher)
        if (LogListBox.Items.Count > 0)
            LogListBox.ScrollIntoView(LogListBox.Items[^1]);
    }

    private void OnScrollToLogEntry(LogEntryViewModel entry)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (LogListBox.Items.Contains(entry))
            {
                LogListBox.ScrollIntoView(entry);
                LogListBox.SelectedItem = entry;
            }
        });
    }

    /// <summary>Copies selected log entries to clipboard (from context menu).</summary>
    private void OnCopySelectedLog(object sender, RoutedEventArgs e)
    {
        var selected = LogListBox.SelectedItems;
        if (selected.Count == 0) return;

        var sb = new StringBuilder();
        foreach (var item in selected)
        {
            if (item is LogEntryViewModel entry)
                sb.AppendLine(entry.FullText);
        }

        if (sb.Length == 0) return;

        try
        {
            Clipboard.SetText(sb.ToString());
            _vm.StatusMessage = $"Copied {selected.Count} selected log entries to clipboard";
        }
        catch (ExternalException)
        {
            _vm.StatusMessage = "Clipboard is in use by another application";
        }
    }

    // ???????????????????????????????????????????????????????????????
    // CLEANUP
    // ???????????????????????????????????????????????????????????????

    protected override void OnClosed(EventArgs e)
    {
        _vm.ScrollToLogEntry -= OnScrollToLogEntry;
        _vm.PropertyChanged -= OnViewModelPropertyChanged;

        if (_vm.LogBuffer is not null)
            _vm.LogBuffer.BatchFlushed -= OnLogBatchFlushed;

        WatchListTreeView.SelectedItemChanged -= OnWatchListSelectionChanged;
        WatchListTreeView.ContextMenuOpening -= OnWatchListContextMenuOpening;
        WatchListTreeView.PreviewMouseLeftButtonDown -= OnTreePreviewMouseDown;
        WatchListTreeView.PreviewMouseMove -= OnTreePreviewMouseMove;
        WatchListTreeView.DragOver -= OnTreeDragOver;
        WatchListTreeView.Drop -= OnTreeDrop;

        TemplateTreeView.SelectedItemChanged -= OnTemplateSelectionChanged;
        TemplateTreeView.ContextMenuOpening -= OnTemplateContextMenuOpening;

        Loaded -= OnWindowLoaded;

        if (_inlineFoldingManager is not null)
            FoldingManager.Uninstall(_inlineFoldingManager);

        _vm.Dispose();
        base.OnClosed(e);
    }
}
