using TestAgentGrpc.Clients;
using TestAgentGrpc.Services;

namespace TestAgentGrpc.UI;

/// <summary>
/// System-tray application context with real-time execution monitoring.
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
    private readonly ILogger<TrayApplicationContext> _logger;

    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _stateItem;
    private readonly ToolStripMenuItem _activityItem;
    private readonly ToolStripMenuItem _metricsItem;
    private readonly ContextMenuStrip _contextMenu;

    private ExecutionMonitorForm? _monitorForm;

    public TrayApplicationContext(
        CommandExecutor executor,
        EventBroadcaster broadcaster,
        ExecutionTracker tracker,
        SystemMetricsCollector metrics,
        ILogger<TrayApplicationContext> logger)
    {
        _executor    = executor;
        _broadcaster = broadcaster;
        _tracker     = tracker;
        _metrics     = metrics;
        _logger      = logger;

        // ── Context menu ───────────────────────────────────────────
        _stateItem    = new ToolStripMenuItem("State: Ready") { Enabled = false };
        _activityItem = new ToolStripMenuItem("Listening...") { Enabled = false };
        _metricsItem  = new ToolStripMenuItem("CPU: — | Mem: —") { Enabled = false };

        _contextMenu = new ContextMenuStrip();
        _contextMenu.Items.Add(_stateItem);
        _contextMenu.Items.Add(_activityItem);
        _contextMenu.Items.Add(_metricsItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("Execution Monitor", null, (_, _) => ShowMonitor());
        _contextMenu.Items.Add("Refresh", null, (_, _) => RefreshAll());
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("Exit", null, OnExit);

        // ── Notify icon ────────────────────────────────────────────
        _notifyIcon = new NotifyIcon
        {
            Text = "TestAgent (gRPC)",
            Icon = SystemIcons.Application,
            ContextMenuStrip = _contextMenu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowMonitor();

        // ── Subscribe to events ────────────────────────────────────
        _executor.StateChanged   += (_, s) => SafeInvoke(() => UpdateState(s));
        _executor.ActivityChanged += (_, a) => SafeInvoke(() => UpdateActivity(a));

        // Background metrics refresh for the tray tooltip
        _ = Task.Run(MetricsRefreshLoop);
    }

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

    private async Task MetricsRefreshLoop()
    {
        while (!IsDisposed)
        {
            try
            {
                await Task.Delay(5000);
                var m = _metrics.Collect();
                SafeInvoke(() =>
                    _metricsItem.Text = $"CPU: {m.CpuUsagePct}% | Mem: {m.MemoryUsedMb}MB | Disk: {m.DiskFreeGb}GB");
            }
            catch { }
        }
    }

    private void RefreshAll()
    {
        UpdateState(_executor.CurrentState);
        UpdateActivity(_executor.Activity);
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

    private async void OnExit(object? s, EventArgs e)
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _monitorForm?.Close();
        ExitThread();
    }
}
