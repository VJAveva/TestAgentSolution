using System.Drawing;
using System.IO;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace TestControllerGrpc.Services;

/// <summary>
/// Encapsulates a Windows system-tray <see cref="WinForms.NotifyIcon"/> attached to a WPF window.
/// Architecture goals:
///   ? The window keeps no knowledge of WinForms types: this service hides the bridge.
///   ? Icon loading is deterministic and never throws (falls back to <see cref="SystemIcons.Application"/>).
///   ? One service instance per window, owned by the window; <see cref="IDisposable"/>.
///   ? Suitable to be created from <c>App.OnStartup</c> in a future standalone Dashboard project,
///     so the existing WPF host doesn't pay the cost when the dashboard is closed.
///
/// Usage:
/// <code>
///   _tray = new TrayIconService(window, "Execution Dashboard", iconRelativePath: "Assets/dashboard.ico");
///   _tray.Install();
///   // window.OnClosing -> if (!AllowClose) { e.Cancel = true; Hide(); _tray.ShowBalloon(...); }
/// </code>
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Window _window;
    private readonly string _tooltip;
    private readonly string _iconRelativePath;
    private WinForms.NotifyIcon? _notify;
    private bool _disposed;

    /// <summary>Raised when the user picks "Exit" from the tray context menu.</summary>
    public event EventHandler? ExitRequested;

    public TrayIconService(Window window, string tooltip = "Execution Dashboard",
        string iconRelativePath = "Assets/dashboard.ico")
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _tooltip = tooltip;
        _iconRelativePath = iconRelativePath;
    }

    /// <summary>Creates the tray icon and wires the context menu / double-click handlers.</summary>
    public void Install()
    {
        if (_notify != null) return;

        _notify = new WinForms.NotifyIcon
        {
            Text = _tooltip,
            Icon = LoadIcon(_iconRelativePath),
            Visible = true,
        };

        var menu = new WinForms.ContextMenuStrip();
        var showItem = menu.Items.Add("Show Dashboard");
        showItem.Click += (_, _) => Restore();
        menu.Items.Add(new WinForms.ToolStripSeparator());
        var exitItem = menu.Items.Add("Exit");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        _notify.ContextMenuStrip = menu;

        _notify.DoubleClick += (_, _) => Restore();
    }

    /// <summary>Shows a balloon tip near the tray icon (e.g. on hide-to-tray).</summary>
    public void ShowBalloon(string title, string message,
        WinForms.ToolTipIcon icon = WinForms.ToolTipIcon.Info, int timeoutMs = 3000)
    {
        if (_notify == null) return;
        _notify.BalloonTipTitle = title;
        _notify.BalloonTipText = message;
        _notify.BalloonTipIcon = icon;
        _notify.ShowBalloonTip(timeoutMs);
    }

    /// <summary>Restores the window from tray (show + activate + bring to front).</summary>
    public void Restore()
    {
        _window.Dispatcher.Invoke(() =>
        {
            if (!_window.IsVisible) _window.Show();
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
            _window.Topmost = true;   // briefly to bring to front?
            _window.Topmost = false;  // ?then release
            _window.Focus();
        });
    }

    /// <summary>
    /// Resolves the icon with a deterministic fallback chain:
    ///   1. <c>{exeDir}/{iconRelativePath}</c>
    ///   2. <c>pack://application:,,,/{iconRelativePath}</c>
    ///   3. <see cref="SystemIcons.Application"/>
    /// </summary>
    private static Icon LoadIcon(string relativePath)
    {
        // 1. File next to the executable
        try
        {
            var exeDir = AppContext.BaseDirectory;
            var fsPath = Path.Combine(exeDir, relativePath);
            if (File.Exists(fsPath))
                return new Icon(fsPath);
        }
        catch { /* fall through */ }

        // 2. WPF resource pack URI (icon embedded as Resource in .csproj)
        try
        {
            var uri = new Uri($"pack://application:,,,/{relativePath}", UriKind.Absolute);
            var info = System.Windows.Application.GetResourceStream(uri);
            if (info?.Stream != null)
            {
                using var s = info.Stream;
                return new Icon(s);
            }
        }
        catch { /* fall through */ }

        // 3. Last resort
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_notify != null)
        {
            _notify.Visible = false;
            _notify.ContextMenuStrip?.Dispose();
            _notify.Dispose();
            _notify = null;
        }
    }
}
