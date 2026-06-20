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
    private readonly MainViewModel _vm;
    private readonly Services.CapabilityChecker _capabilityChecker;

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
    private double _logPaneMaxHeightPercent = 0.70;

    // ?? System tray ??????????????????????????????????????????
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _isActuallyExiting;

    // ?? Mode-switch lifecycle ??????????????????????????????????
    private readonly SystemModeClient? _systemModeClient;

    public MainWindow()
    {
        InitializeComponent();
        LoadDesignTokens();
        _vm = App.Services.GetRequiredService<MainViewModel>();
        _capabilityChecker = App.Services.GetRequiredService<Services.CapabilityChecker>();
        _vm.EnsureSubscriptions();
        DataContext = _vm;

        // Phase 1b: Users tab visibility now driven by CapabilityChecker via
        // OnCapabilitiesChanged → RefreshUsersTabVisibility() in MainViewModel.
        // Initial state is set in EnsureSubscriptions().

        // Refresh Default-mode banner on live mode switch
        _systemModeClient = App.Services.GetService<SystemModeClient>();
        if (_systemModeClient is not null)
        {
            _systemModeClient.ModeChanged += OnModeChanged;
        }

        Closed += OnWindowClosed;

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

        // ?? Window icon (matches the system tray "TC" badge) ?????????
        Icon = CreateControllerWindowImage();

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
        using var bmp = CreateControllerBadgeBitmap(16);
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>
    /// Creates the window icon from the SAME "TC" badge artwork used by the tray,
    /// so the title-bar/taskbar icon and the tray icon are identical (rendered at
    /// 32x32 for crisp display in the taskbar and Alt+Tab).
    /// </summary>
    private static System.Windows.Media.ImageSource CreateControllerWindowImage()
    {
        using var bmp = CreateControllerBadgeBitmap(32);
        var hIcon = bmp.GetHicon();
        try
        {
            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                hIcon,
                System.Windows.Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    /// <summary>
    /// Renders the Controller's "TC" badge (rounded blue square with white "TC")
    /// at the requested size. Shared by the tray icon and the window icon so they
    /// always match.
    /// </summary>
    private static System.Drawing.Bitmap CreateControllerBadgeBitmap(int size)
    {
        var bmp = new System.Drawing.Bitmap(size, size);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(System.Drawing.Color.Transparent);

        float radius = size * 0.22f;
        using (var path = new System.Drawing.Drawing2D.GraphicsPath())
        {
            float d = radius * 2f;
            float max = size - 1f;
            path.AddArc(0, 0, d, d, 180, 90);
            path.AddArc(max - d, 0, d, d, 270, 90);
            path.AddArc(max - d, max - d, d, d, 0, 90);
            path.AddArc(0, max - d, d, d, 90, 90);
            path.CloseFigure();
            using var bg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(30, 100, 200));
            g.FillPath(bg, path);
        }

        using var font = new System.Drawing.Font("Segoe UI", size * 0.42f, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
        using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.White);
        var sf = new System.Drawing.StringFormat
        {
            Alignment = System.Drawing.StringAlignment.Center,
            LineAlignment = System.Drawing.StringAlignment.Center
        };
        g.DrawString("TC", font, brush, new System.Drawing.RectangleF(0, 0, size, size), sf);
        return bmp;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

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
    // MODE-SWITCH LIFECYCLE
    // ???????????????????????????????????????????????????????????????

    private void OnModeChanged(string mode)
    {
        if (Dispatcher.CheckAccess())
        {
            HandleModeSwitch(mode);
        }
        else
        {
            Dispatcher.Invoke(() => HandleModeSwitch(mode));
        }
    }

    private void HandleModeSwitch(string mode)
    {
        // Derive from the authoritative mode param, not IOptionsMonitor (may be stale)
        bool isSecured = string.Equals(mode, "secured", StringComparison.OrdinalIgnoreCase);

        // Set authoritative mode on holder BEFORE any user/capability refresh
        var holder = App.Services.GetRequiredService<CurrentUserHolder>();
        holder.SetMode(isSecured);

        // Update ViewModel mode state — drives banner visibility
        _vm.IsDefaultMode = !isSecured;

        if (isSecured)
        {
            // Clear identity — user must log in again
            holder.Clear();

            // Explicitly refresh badge/tab to "no user" state
            _vm.EnsureSubscriptions();

            // Hide MainWindow (reuse on next login) and show LoginPage
            Hide();
            ShowInTaskbar = false;
            var loginPage = new Login.LoginPage();
            loginPage.Show();
        }
        else
        {
            // Default mode: clear identity, stay on MainWindow as Default user
            var authClient = App.Services.GetRequiredService<AuthClient>();
            _ = authClient.LogoutAsync(); // best-effort server-side cleanup
            holder.SetDefaultUser();

            // Explicitly refresh badge/tab to Default user state
            _vm.EnsureSubscriptions();
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        // Prevent handler leak across repeated mode switches
        if (_systemModeClient is not null)
            _systemModeClient.ModeChanged -= OnModeChanged;
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
}
