using System.Drawing.Drawing2D;
using Microsoft.Extensions.Options;
using TestAgentGrpc.Clients;
using TestAgentGrpc.Services;

namespace TestAgentGrpc.UI;

/// <summary>
/// System-tray application context with real-time execution monitoring,
/// dynamic connection-health icons, and Windows toast notifications.
///
/// Subscribes to the <see cref="EventBroadcaster"/> and displays live
/// stdout/stderr, state transitions, and resource metrics in a dockable
/// monitor form. This replaces the legacy WPF ContextMenuWpf + MainWindow
/// that only showed a flat activity string.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly CommandExecutor _executor;
    private readonly EventBroadcaster _broadcaster;
    private readonly ExecutionTracker _tracker;
    private readonly SystemMetricsCollector _metrics;
    private readonly ConnectionHealthMonitor _healthMonitor;
    private readonly TestControllerClient _controllerClient;
    private readonly AgentSettings _agentSettings;
    private readonly NotificationSettings _notificationSettings;
    private readonly ILogger<TrayApplicationContext> _logger;

    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _connectionItem;
    private readonly ToolStripMenuItem _uptimeItem;
    private readonly ToolStripMenuItem _stateItem;
    private readonly ToolStripMenuItem _activityItem;
    private readonly ToolStripMenuItem _metricsItem;
    private readonly ToolStripMenuItem _lastCommandItem;
    private readonly ContextMenuStrip _contextMenu;
    private readonly System.Windows.Forms.Timer _menuRefreshTimer;

    private ExecutionMonitorForm? _monitorForm;
    private ConnectionDetailForm? _connectionDetailForm;

    // Cached icons for each connection state
    private readonly Icon _greenIcon;
    private readonly Icon _yellowIcon;
    private readonly Icon _redIcon;
    private readonly Icon _grayIcon;

    public TrayApplicationContext(
        CommandExecutor executor,
        EventBroadcaster broadcaster,
        ExecutionTracker tracker,
        SystemMetricsCollector metrics,
        ConnectionHealthMonitor healthMonitor,
        TestControllerClient controllerClient,
        IOptions<AgentSettings> agentSettings,
        IOptions<NotificationSettings> notificationSettings,
        ILogger<TrayApplicationContext> logger)
    {
        _executor             = executor;
        _broadcaster          = broadcaster;
        _tracker              = tracker;
        _metrics              = metrics;
        _healthMonitor        = healthMonitor;
        _controllerClient     = controllerClient;
        _agentSettings        = agentSettings.Value;
        _notificationSettings = notificationSettings.Value;
        _logger               = logger;

        // ── Pre-create colored icons ───────────────────────────────
        _greenIcon  = CreateColoredIcon(Color.FromArgb(0, 200, 80));
        _yellowIcon = CreateColoredIcon(Color.FromArgb(240, 200, 0));
        _redIcon    = CreateColoredIcon(Color.FromArgb(220, 40, 40));
        _grayIcon   = CreateColoredIcon(Color.FromArgb(140, 140, 140));

        // ── Context menu ───────────────────────────────────────────
        _connectionItem  = new ToolStripMenuItem("● Not registered") { Enabled = false };
        _uptimeItem      = new ToolStripMenuItem("Uptime: — | Heartbeats: 0") { Enabled = false };
        _stateItem       = new ToolStripMenuItem("State: Ready") { Enabled = false };
        _activityItem    = new ToolStripMenuItem("Listening...") { Enabled = false };
        _metricsItem     = new ToolStripMenuItem("CPU: — | Mem: —") { Enabled = false };
        _lastCommandItem = new ToolStripMenuItem("Last Command: —") { Enabled = false };

        _contextMenu = new ContextMenuStrip();
        _contextMenu.Items.Add(_connectionItem);
        _contextMenu.Items.Add(_uptimeItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(_stateItem);
        _contextMenu.Items.Add(_activityItem);
        _contextMenu.Items.Add(_metricsItem);
        _contextMenu.Items.Add(_lastCommandItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("Execution Monitor", null, (_, _) => ShowMonitor());
        _contextMenu.Items.Add("View Audit Log", null, (_, _) => OpenAuditLogDirectory());
        _contextMenu.Items.Add("Connection Details...", null, (_, _) => ShowConnectionDetails());
        _contextMenu.Items.Add("Export Report...", null, (_, _) => ExportReport());
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("Settings", null, (_, _) => OpenSettings());
        _contextMenu.Items.Add("Refresh", null, (_, _) => RefreshAll());
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("Exit", null, OnExit);

        // ── Notify icon ────────────────────────────────────────────
        _notifyIcon = new NotifyIcon
        {
            Text = "TestAgent (gRPC)",
            Icon = _grayIcon,
            ContextMenuStrip = _contextMenu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowMonitor();

        // ── Subscribe to events ────────────────────────────────────
        _executor.StateChanged    += (_, s) => SafeInvoke(() => UpdateState(s));
        _executor.ActivityChanged += (_, a) => SafeInvoke(() => UpdateActivity(a));

        _healthMonitor.ConnectionStatusChanged += status => SafeInvoke(() => UpdateTrayIcon(status));
        _healthMonitor.ConnectionLost          += OnConnectionLost;
        _healthMonitor.ConnectionRecovered     += OnConnectionRecovered;

        // ── Menu refresh timer (updates text, does not rebuild) ────
        _menuRefreshTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _menuRefreshTimer.Tick += (_, _) => RefreshMenuText();
        _menuRefreshTimer.Start();
    }

    // ── Dynamic tray icon ──────────────────────────────────────────────

    private static Icon CreateColoredIcon(Color color)
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, 2, 2, 12, 12);
        using var pen = new Pen(Color.White, 1);
        g.DrawEllipse(pen, 2, 2, 12, 12);
        var icon = Icon.FromHandle(bmp.GetHicon());
        return icon;
    }

    private void UpdateTrayIcon(ConnectionStatus status)
    {
        _notifyIcon.Icon = status switch
        {
            ConnectionStatus.Connected       => _greenIcon,
            ConnectionStatus.Warning         => _yellowIcon,
            ConnectionStatus.Disconnected    => _redIcon,
            ConnectionStatus.NeverRegistered => _grayIcon,
            _ => _grayIcon,
        };
    }

    // ── Toast notifications ────────────────────────────────────────────

    private void OnConnectionLost()
    {
        if (!_notificationSettings.NotifyOnControllerLost) return;
        SafeInvoke(() =>
        {
            _notifyIcon.ShowBalloonTip(5000,
                "TestAgent — Controller Lost",
                $"Lost connection to {_healthMonitor.ControllerName} " +
                $"({_healthMonitor.ControllerAddress}) after {_healthMonitor.ConsecutiveFailures} " +
                "failed heartbeats.\nAgent continues in standalone mode.",
                ToolTipIcon.Error);
        });
    }

    private void OnConnectionRecovered()
    {
        if (!_notificationSettings.NotifyOnControllerRecovered) return;
        SafeInvoke(() =>
        {
            var downtime = _healthMonitor.DowntimeDuration;
            var downtimeText = downtime is { } dt ? dt.ToString(@"mm\:ss") : "—";
            _notifyIcon.ShowBalloonTip(5000,
                "TestAgent — Controller Recovered",
                $"Reconnected to {_healthMonitor.ControllerName}. " +
                $"Downtime: {downtimeText}",
                ToolTipIcon.Info);
        });
    }

    // ── Menu text updates (runs on timer, no rebuild) ──────────────────

    private void RefreshMenuText()
    {
        try
        {
            // Connection status line
            var status = _healthMonitor.GetStatus();
            var controllerLabel = _healthMonitor.ControllerName ?? "controller";
            _connectionItem.Text = status switch
            {
                ConnectionStatus.Connected    => $"● Connected to {controllerLabel}",
                ConnectionStatus.Warning      => $"● Warning — {controllerLabel} ({_healthMonitor.ConsecutiveFailures} failures)",
                ConnectionStatus.Disconnected => $"● Disconnected from {controllerLabel}",
                _                             => "● Not registered",
            };

            // Uptime / heartbeats line
            var uptime = _healthMonitor.UptimeSinceLastRecovery;
            var uptimeStr = uptime.TotalHours >= 1
                ? uptime.ToString(@"hh\:mm\:ss")
                : uptime.ToString(@"mm\:ss");
            _uptimeItem.Text = $"Uptime: {uptimeStr} | Heartbeats: {_healthMonitor.TotalHeartbeatsSent}";

            // Metrics
            var m = _metrics.Collect();
            _metricsItem.Text = $"CPU: {m.CpuUsagePct}% | Mem: {m.MemoryUsedMb}MB | Disk: {m.DiskFreeGb}GB";

            // Last command from execution history
            var history = _tracker.GetHistory(max: 1);
            if (history.Count > 0)
            {
                var last = history.First();
                var finishedTime = last.Finished?.ToDateTime().ToLocalTime().ToString("T") ?? "—";
                _lastCommandItem.Text = $"Last Command: {finishedTime} (exit {last.ExitCode})";
            }
        }
        catch
        {
            // Best-effort refresh — don't crash the tray
        }
    }

    // ── State / activity updates ───────────────────────────────────────

    private void UpdateState(AgentState state)
    {
        var label = state switch
        {
            AgentState.Ready   => "Ready",
            AgentState.Running => "Running",
            _                  => "Inactive",
        };
        _stateItem.Text = $"State: {label}";
        _notifyIcon.Text = $"TestAgent — {label}";
    }

    private void UpdateActivity(string activity)
    {
        _activityItem.Text = activity.Length > 60 ? $"{activity[..57]}…" : activity;
    }

    private void RefreshAll()
    {
        UpdateState(_executor.CurrentState);
        UpdateActivity(_executor.Activity);
        RefreshMenuText();
    }

    private bool IsDisposed => _notifyIcon?.Icon is null;

    // ── Monitor form ───────────────────────────────────────────────────

    private void ShowMonitor()
    {
        if (_monitorForm is { IsDisposed: false })
        {
            _monitorForm.Activate();
            return;
        }

        _monitorForm = new ExecutionMonitorForm(_executor, _broadcaster, _tracker, _metrics);
        _monitorForm.Show();
    }

    // ── Connection details form ────────────────────────────────────────

    private void ShowConnectionDetails()
    {
        if (_connectionDetailForm is { IsDisposed: false })
        {
            _connectionDetailForm.Activate();
            return;
        }

        _connectionDetailForm = new ConnectionDetailForm(
            _healthMonitor, _agentSettings, _controllerClient, _metrics);
        _connectionDetailForm.Show();
    }

    // ── Export Report ─────────────────────────────────────────────────

    private void ExportReport()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Export Execution Report",
            Filter = "CSV files|*.csv|JSON files|*.json|HTML files|*.html",
            FileName = $"report_{_agentSettings.AgentName}_{DateTime.Now:yyyyMMdd_HHmmss}",
            FilterIndex = 3, // default HTML
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        try
        {
            var records = _tracker.GetHistory(max: 0); // 0 = all
            var content = Path.GetExtension(dlg.FileName).ToLowerInvariant() switch
            {
                ".csv"  => ReportGenerator.GenerateCsv(records),
                ".json" => ReportGenerator.GenerateJson(_agentSettings.AgentName, records),
                ".html" => ReportGenerator.GenerateHtml(_agentSettings.AgentName, records),
                _       => ReportGenerator.GenerateHtml(_agentSettings.AgentName, records),
            };
            File.WriteAllText(dlg.FileName, content);
            MessageBox.Show($"Report exported: {records.Count} executions.\n{dlg.FileName}",
                "Export Report", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Report export failed");
            MessageBox.Show($"Export failed: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ── Menu actions ───────────────────────────────────────────────────

    private void OpenAuditLogDirectory()
    {
        try
        {
            var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
            if (!Directory.Exists(logDir))
                logDir = AppContext.BaseDirectory;
            System.Diagnostics.Process.Start("explorer.exe", logDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open audit log directory");
        }
    }

    private void OpenSettings()
    {
        try
        {
            var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(settingsPath))
                System.Diagnostics.Process.Start("notepad.exe", settingsPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open settings file");
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private void SafeInvoke(Action action)
    {
        try
        {
            if (_contextMenu.InvokeRequired)
                _contextMenu.Invoke(action);
            else
                action();
        }
        catch { }
    }

    private void OnExit(object? s, EventArgs e)
    {
        _menuRefreshTimer.Stop();
        _menuRefreshTimer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _monitorForm?.Close();
        _connectionDetailForm?.Close();
        ExitThread();
    }
}
