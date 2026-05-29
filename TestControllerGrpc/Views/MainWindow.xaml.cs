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
using TestControllerGrpc.ViewModels;

using TestControllerGrpc.Views.Dialogs;

namespace TestControllerGrpc.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    // ?? Inline AvalonEdit editor ?????????????????????????????????????
    private TextEditor? _inlineEditor;
    private FoldingManager? _inlineFoldingManager;
    private XmlFoldingStrategy? _inlineFoldingStrategy;

    // ?? Drag-and-drop state ??????????????????????????????????????
    private Point _dragStartPoint;
    private TreeNodeViewModel? _draggedNode;
    #pragma warning disable CS0414 // assigned but never read � used for drag-and-drop state tracking
        private bool _isDragging;
    #pragma warning restore CS0414

    // ── Dockable pane saved sizes ──────────────────────────────────────────
    private GridLength _savedAgentColWidth = new(3, GridUnitType.Star);
    private GridLength _savedTreeColWidth = new(2.5, GridUnitType.Star);
    private GridLength _savedPropertiesColWidth = new(5, GridUnitType.Star);
    private GridLength _savedLogRowHeight = new(2, GridUnitType.Star);

    // ── Panel sizing tokens (loaded from DesignTokens.xaml) ────────────────
    private double _treePanelMinWidth = 220;
    private double _agentPanelMinPinned = 280;
    private double _agentPaneCollapsedWidth = 28;
    private double _logPaneMinHeight = 120;
    private double _logPaneCollapsedHeight = 28;
    private double _treePanelMaxWidthPercent = 0.35;
    private double _agentPanelMaxWidthPercent = 0.35;
    private double _logPaneMaxHeightPercent = 0.40;

    // ?? System tray ??????????????????????????????????????????
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _isActuallyExiting;

    public MainWindow()
    {
        InitializeComponent();
        LoadDesignTokens();
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

        // ?? Drag-and-drop handlers ??????????????????????????
        WatchListTreeView.PreviewMouseLeftButtonDown += OnTreePreviewMouseDown;
        WatchListTreeView.PreviewMouseMove += OnTreePreviewMouseMove;
        WatchListTreeView.DragOver += OnTreeDragOver;
        WatchListTreeView.Drop += OnTreeDrop;

        // ?? Inline AvalonEdit setup ?????????????????????????
        SetupInlineXmlEditor();
        _vm.PropertyChanged += OnViewModelPropertyChanged;

        // ?? Keyboard shortcut support ????????????????????????
        _vm.FocusLogSearchRequested += OnFocusLogSearchRequested;

        // ?? System tray setup ???????????????????????????????
        SetupTrayIcon();
        Closing += OnWindowClosing;
        System.Windows.Application.Current.Exit += (_, _) => _trayIcon?.Dispose();
    }

    // ???????????????????????????????????????????????????????????????
    // SYSTEM TRAY
    // ???????????????????????????????????????????????????????????????

    private void SetupTrayIcon()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "TestController — Main",
            Visible = false,
        };

        _trayIcon.Icon = CreateControllerTrayIcon();

        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Restore Window", null, (_, _) => RestoreFromTray());
        menu.Items.Add("-");

        var statusItem = new System.Windows.Forms.ToolStripMenuItem("Status: Idle") { Enabled = false };
        menu.Items.Add(statusItem);
        menu.Items.Add("-");

        menu.Items.Add("Exit TestController", null, (_, _) =>
        {
            var vm = DataContext as MainViewModel;
            bool hasActive = vm?.IsExecuting == true;

            if (hasActive)
            {
                var result = ThemedMessageBox.Show(
                    "A pipeline is still running.\n\n" +
                    "Exiting will cancel all running executions.\n\n" +
                    "Continue?",
                    "Confirm Exit",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes) return;
            }

            _isActuallyExiting = true;
            _trayIcon?.Dispose();
            System.Windows.Application.Current.Shutdown();
        });

        _trayIcon.ContextMenuStrip = menu;

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        timer.Tick += (_, _) =>
        {
            var vm = DataContext as MainViewModel;
            if (vm == null) return;

            var status = vm.IsExecuting
                ? $"Running: {vm.StatusMessage}"
                : "Idle";

            _trayIcon.Text = $"TestController - {status}"[..Math.Min(63,
                $"TestController - {status}".Length)];
            statusItem.Text = $"Status: {status}";
        };
        timer.Start();
    }

    /// <summary>
    /// Creates a distinct 16x16 tray icon for the Controller (blue "TC" badge)
    /// so it's visually distinguishable from the Execution Dashboard icon.
    /// </summary>
    private static System.Drawing.Icon CreateControllerTrayIcon()
    {
        using var bmp = new System.Drawing.Bitmap(16, 16);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(System.Drawing.Color.FromArgb(30, 100, 200)); // blue background
        using var font = new System.Drawing.Font("Segoe UI", 7f, System.Drawing.FontStyle.Bold);
        using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.White);
        var sf = new System.Drawing.StringFormat
        {
            Alignment = System.Drawing.StringAlignment.Center,
            LineAlignment = System.Drawing.StringAlignment.Center
        };
        g.DrawString("TC", font, brush, new System.Drawing.RectangleF(0, 0, 16, 16), sf);
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_isActuallyExiting) return;

        e.Cancel = true;
        MinimizeToTray();
    }

    private void MinimizeToTray()
    {
        Hide();
        ShowInTaskbar = false;

        if (_trayIcon != null)
        {
            _trayIcon.Visible = true;

            var vm = DataContext as MainViewModel;
            var balloonText = vm?.IsExecuting == true
                ? "Pipeline still running in background.\nDouble-click to restore."
                : "Running in background.\nDouble-click to restore.";

            _trayIcon.ShowBalloonTip(
                3000,
                "TestController",
                balloonText,
                System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    private void RestoreFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            ShowInTaskbar = true;
            WindowState = WindowState.Normal;
            Activate();

            if (_trayIcon != null)
                _trayIcon.Visible = false;
        });
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
        else if (e.PropertyName == nameof(MainViewModel.IsTreePanePinned))
        {
            ApplyTreePaneLayout(_vm.IsTreePanePinned);
        }
    }

    private void ApplyAgentPaneLayout(bool pinned)
    {
        if (pinned)
        {
            ColAgentPanel.Width = _savedAgentColWidth;
            ColAgentPanel.MinWidth = _agentPanelMinPinned;
            ColAgentSplitter.Width = GridLength.Auto;
            ColNodeProperties.Width = _savedPropertiesColWidth;
        }
        else
        {
            if (ColAgentPanel.Width.IsStar)
                _savedAgentColWidth = ColAgentPanel.Width;
            if (ColNodeProperties.Width.IsStar)
                _savedPropertiesColWidth = ColNodeProperties.Width;

            ColAgentPanel.Width = GridLength.Auto;
            ColAgentPanel.MinWidth = _agentPaneCollapsedWidth;
            ColAgentSplitter.Width = new GridLength(0);
            ColNodeProperties.Width = new GridLength(1, GridUnitType.Star);

            // Move focus to properties panel so keyboard users aren't stranded
            Dispatcher.InvokeAsync(() =>
            {
                var propertiesPanel = MainContentGrid.Children
                    .OfType<FrameworkElement>()
                    .FirstOrDefault(c => Grid.GetColumn(c) == 2 && Grid.GetRow(c) == 0);
                propertiesPanel?.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void ApplyLogPaneLayout(bool pinned)
    {
        if (pinned)
        {
            RowLogPane.Height = _savedLogRowHeight;
            RowLogPane.MinHeight = _logPaneMinHeight;
        }
        else
        {
            if (RowLogPane.Height.IsStar)
                _savedLogRowHeight = RowLogPane.Height;

            RowLogPane.Height = GridLength.Auto;
            RowLogPane.MinHeight = _logPaneCollapsedHeight;
        }
    }

    private void ApplyTreePaneLayout(bool pinned)
    {
        if (pinned)
        {
            ColTreePanel.Width = _savedTreeColWidth;
            ColTreePanel.MinWidth = _treePanelMinWidth;
        }
        else
        {
            if (ColTreePanel.Width.IsStar)
                _savedTreeColWidth = ColTreePanel.Width;

            ColTreePanel.Width = GridLength.Auto;
            ColTreePanel.MinWidth = _agentPaneCollapsedWidth; // same 28px collapsed width

            // Move keyboard focus to properties panel so user isn't stranded
            Dispatcher.InvokeAsync(() =>
            {
                var propertiesPanel = MainContentGrid.Children
                    .OfType<FrameworkElement>()
                    .FirstOrDefault(c => Grid.GetColumn(c) == 2 && Grid.GetRow(c) == 0);
                propertiesPanel?.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    // ── Design tokens ──────────────────────────────────────────────────────
    private void LoadDesignTokens()
    {
        if (TryFindResource("TreePanelMinWidth") is double treePanelMin)
            _treePanelMinWidth = treePanelMin;
        if (TryFindResource("AgentPanelMinPinned") is double agentMin)
            _agentPanelMinPinned = agentMin;
        if (TryFindResource("AgentPaneCollapsedWidth") is double agentCollapsed)
            _agentPaneCollapsedWidth = agentCollapsed;
        if (TryFindResource("LogPaneMinHeight") is double logMin)
            _logPaneMinHeight = logMin;
        if (TryFindResource("LogPaneCollapsedHeight") is double logCollapsed)
            _logPaneCollapsedHeight = logCollapsed;
        if (TryFindResource("TreePanelMaxWidthPercent") is double treeMax)
            _treePanelMaxWidthPercent = treeMax;
        if (TryFindResource("AgentPanelMaxWidthPercent") is double agentMax)
            _agentPanelMaxWidthPercent = agentMax;
        if (TryFindResource("LogPaneMaxHeightPercent") is double logMax)
            _logPaneMaxHeightPercent = logMax;
    }

    // ── MaxWidth / MaxHeight enforcement on resize ─────────────────────────
    private void MainContentGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid grid || grid.ActualWidth < 1 || grid.ActualHeight < 1)
            return;

        // Enforce max proportions using MaxWidth on grid columns.
        // This avoids converting star→pixel which breaks proportional layout.
        double maxTreeWidth = grid.ActualWidth * _treePanelMaxWidthPercent;
        ColTreePanel.MaxWidth = _vm.IsTreePanePinned ? maxTreeWidth : double.PositiveInfinity;

        double maxAgentWidth = grid.ActualWidth * _agentPanelMaxWidthPercent;
        ColAgentPanel.MaxWidth = _vm.IsAgentPanePinned ? maxAgentWidth : double.PositiveInfinity;

        // Log pane: clamp by ensuring main content row doesn't shrink below 250
        if (_vm.IsLogPanePinned)
        {
            double maxLogHeight = grid.ActualHeight * _logPaneMaxHeightPercent;
            double mainRowMinHeight = grid.ActualHeight - maxLogHeight - 4;
            if (mainRowMinHeight > 250)
                MainContentGrid.RowDefinitions[0].MinHeight = mainRowMinHeight;
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
            catch (Exception) { /* XML parse errors expected during mid-edit � folding will retry on next keystroke */ }
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
        // Always rebuild for WatchItem (IsEnabled toggle is node-specific)
        if (nodeKind != _lastWatchListContextMenuNodeKind || _cachedWatchListContextMenu is null
            || nodeKind is "WatchItem")
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
            menu.Items.Add(CreateMenuItemWithIcon("Trigger All Events", _vm.TriggerWatchItemCommand, "\uE768", "AccGreen"));
            menu.Items.Add(new Separator());

            // Toggle: include/exclude this WatchItem from "Trigger All" execution
            var enabledItem = new MenuItem
            {
                Header = "Include in Trigger All",
                IsCheckable = true,
                IsChecked = node.IsEnabled
            };
            var capturedNode = node;
            enabledItem.Checked += (_, _) => capturedNode.IsEnabled = true;
            enabledItem.Unchecked += (_, _) => capturedNode.IsEnabled = false;
            menu.Items.Add(enabledItem);
            menu.Items.Add(new Separator());
        }
        if (node.NodeKind is "Event")
        {
            menu.Items.Add(CreateMenuItemWithIcon("Trigger Event", _vm.TriggerEventCommand, "\uEA80", "AccGreen"));
            menu.Items.Add(new Separator());
        }
        if (node.NodeKind is "ActionGroup")
        {
            menu.Items.Add(CreateMenuItemWithIcon("Execute Group", _vm.ExecuteGroupCommand, "\uE768", "AccGreen"));
            menu.Items.Add(new Separator());
        }
        if (node.NodeKind is "Action")
        {
            menu.Items.Add(CreateMenuItemWithIcon("Execute Action", _vm.ExecuteSingleActionCommand, "\uE768", "AccGreen"));
            menu.Items.Add(new Separator());
        }

        // ?? Add commands (context-sensitive) ??????????????????????????
        if (node.NodeKind is "WatchList")
        {
            menu.Items.Add(CreateMenuItemWithIcon("Add WatchItem", _vm.AddWatchItemCommand, "\uE710", "Accent"));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItemWithIcon("Import WatchItems�", _vm.ImportWatchItemsCommand, "\uE8B5", "Accent"));
            menu.Items.Add(CreateMenuItemWithIcon("Export All WatchItems�", _vm.ExportWatchItemsCommand, "\uE898", "Accent"));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItemWithIcon("Edit WatchList XML�", _vm.OpenWatchListEditorCommand, "\uE70F", "AccMauve"));
        }
        else if (node.NodeKind is "WatchItem")
        {
            menu.Items.Add(CreateMenuItemWithIcon("Add Event", _vm.AddChildNodeCommand, "\uEA80", "AccYellow"));
            menu.Items.Add(CreateMenuItemWithIcon("Add ActionGroup", _vm.AddActionGroupCommand, "\uE8F1", "Accent"));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItemWithIcon("Export WatchItem�", _vm.ExportWatchItemsCommand, "\uE898", "Accent"));
        }
        else if (node.NodeKind is "Event" or "ActionGroup")
        {
            menu.Items.Add(CreateMenuItemWithIcon("Add ActionGroup", _vm.AddActionGroupCommand, "\uE8F1", "Accent"));
            menu.Items.Add(CreateMenuItemWithIcon("Add Action", _vm.AddActionToGroupCommand, "\uE7C8", "AccPeach"));
            menu.Items.Add(CreateMenuItemWithIcon("Add Ref", _vm.AddRefToGroupCommand, "\uE71B", "AccMauve"));
            menu.Items.Add(CreateMenuItemWithIcon("Add Initialize", _vm.AddInitializeToGroupCommand, "\uE713", "AccYellow"));
        }

        // ?? Change ExecutionType submenu (Event / ActionGroup) ??????
        if (node.NodeKind is "Event" or "ActionGroup")
        {
            menu.Items.Add(new Separator());
            var execTypeSubmenu = BuildExecutionTypeSubmenu(node);
            execTypeSubmenu.Icon = new System.Windows.Controls.TextBlock
            {
                Text = "\uE8AB",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = (Brush)FindResource("TextS"),
            };
            menu.Items.Add(execTypeSubmenu);
        }

        // ?? Delete ??????????????????????????????????????????????????
        if (node.NodeKind is not "WatchList")
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItemWithIcon("Delete", _vm.ConfirmDeleteSelectedNodeCommand, "\uE74D", "AccRed"));
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
                    var retryItem = CreateMenuItemWithIcon(
                        $"Retry Failed ({lastSession.FailedCount} action{(lastSession.FailedCount > 1 ? "s" : "")})",
                        _vm.RetryFailedCommand, "\uE72C", "AccPeach");
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
                var moveUp = CreateMenuItemWithIcon("Move Up", null!, "\uE74A", "TextS");
                moveUp.Command = null;
                var capturedNode = node;
                moveUp.Click += (_, _) => _vm.MoveNodeUp(capturedNode);
                menu.Items.Add(moveUp);
            }

            if (MainViewModel.CanMoveNode(node, +1))
            {
                var moveDown = CreateMenuItemWithIcon("Move Down", null!, "\uE74B", "TextS");
                moveDown.Command = null;
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
            menu.Items.Add(CreateMenuItemWithIcon("Add Template", _vm.AddTemplateCommand, "\uE710", "AccMauve"));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItemWithIcon("Edit Template XML", _vm.EditTemplateXmlCommand, "\uE70F", "AccMauve"));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItemWithIcon("Import Templates\u2026", _vm.ImportTemplatesCommand, "\uE8B5", "AccMauve"));
            menu.Items.Add(CreateMenuItemWithIcon("Export All Templates\u2026", _vm.ExportTemplatesCommand, "\uE898", "AccMauve"));
        }
        else if (node.NodeKind is "Template")
        {
            menu.Items.Add(CreateMenuItemWithIcon("Add ActionGroup", _vm.AddGroupToTemplateCommand, "\uE8F1", "Accent"));
            menu.Items.Add(CreateMenuItemWithIcon("Add Action", _vm.AddActionToTemplateCommand, "\uE7C8", "AccPeach"));
            menu.Items.Add(CreateMenuItemWithIcon("Add Ref", _vm.AddRefToTemplateCommand, "\uE71B", "AccMauve"));
            menu.Items.Add(CreateMenuItemWithIcon("Add Initialize", _vm.AddInitializeToTemplateCommand, "\uE713", "AccYellow"));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItemWithIcon("Export Template\u2026", _vm.ExportTemplatesCommand, "\uE898", "AccMauve"));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItemWithIcon("Delete", _vm.DeleteTemplateCommand, "\uE74D", "AccRed"));
        }
        else if (node.NodeKind is "ActionGroup")
        {
            menu.Items.Add(CreateMenuItemWithIcon("Add Action", _vm.AddActionToTemplateCommand, "\uE7C8", "AccPeach"));
            menu.Items.Add(CreateMenuItemWithIcon("Add ActionGroup", _vm.AddGroupToTemplateCommand, "\uE8F1", "Accent"));
            menu.Items.Add(CreateMenuItemWithIcon("Add Ref", _vm.AddRefToTemplateCommand, "\uE71B", "AccMauve"));
            menu.Items.Add(new Separator());
            var execTypeSubmenu = BuildExecutionTypeSubmenu(node);
            execTypeSubmenu.Icon = new System.Windows.Controls.TextBlock
            {
                Text = "\uE8AB",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = (Brush)FindResource("TextS"),
            };
            menu.Items.Add(execTypeSubmenu);
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItemWithIcon("Delete", _vm.DeleteTemplateCommand, "\uE74D", "AccRed"));
        }
        else if (node.NodeKind is not "TemplateList")
        {
            menu.Items.Add(CreateMenuItemWithIcon("Delete", _vm.DeleteTemplateCommand, "\uE74D", "AccRed"));
        }

        // ?? Move Up / Down for templates ????????????????????????????
        if (CanShowMoveItems(node))
        {
            menu.Items.Add(new Separator());

            if (MainViewModel.CanMoveNode(node, -1))
            {
                var moveUp = CreateMenuItemWithIcon("Move Up", null!, "\uE74A", "TextS");
                moveUp.Command = null;
                var capturedNode = node;
                moveUp.Click += (_, _) => _vm.MoveNodeUp(capturedNode);
                menu.Items.Add(moveUp);
            }

            if (MainViewModel.CanMoveNode(node, +1))
            {
                var moveDown = CreateMenuItemWithIcon("Move Down", null!, "\uE74B", "TextS");
                moveDown.Command = null;
                var capturedNode = node;
                moveDown.Click += (_, _) => _vm.MoveNodeDown(capturedNode);
                menu.Items.Add(moveDown);
            }
        }

        return menu;
    }

    private static bool CanShowMoveItems(TreeNodeViewModel node)
    {
        // Initialize is always first � no move
        if (node.NodeKind is "WatchList" or "TemplateList" or "Initialize") return false;
        return MainViewModel.CanMoveNode(node, -1) || MainViewModel.CanMoveNode(node, +1);
    }

    private static MenuItem CreateMenuItem(string header, ICommand command)
    {
        return new MenuItem { Header = header, Command = command };
    }

    private MenuItem CreateMenuItemWithIcon(string header, ICommand command, string iconGlyph, string resourceColorKey)
    {
        var item = new MenuItem { Header = header, Command = command };
        item.Icon = new System.Windows.Controls.TextBlock
        {
            Text = iconGlyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = (Brush)FindResource(resourceColorKey),
        };
        return item;
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
    // KEYBOARD SHORTCUT SUPPORT
    // ???????????????????????????????????????????????????????????????

    private void OnFocusLogSearchRequested()
    {
        LogSearchBox.Focus();
        LogSearchBox.SelectAll();
    }

    // ???????????????????????????????????????????????????????????????
    // CLEANUP
    // ???????????????????????????????????????????????????????????????

    protected override void OnClosed(EventArgs e)
    {
        _vm.ScrollToLogEntry -= OnScrollToLogEntry;
        _vm.FocusLogSearchRequested -= OnFocusLogSearchRequested;
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
