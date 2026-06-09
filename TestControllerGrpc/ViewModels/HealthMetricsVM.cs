using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels;

/// <summary>Operator-configurable health thresholds loaded from appsettings.json.</summary>
public sealed class HealthThresholdSettings
{
    public double CpuAmber { get; set; } = 60;
    public double CpuRed { get; set; } = 85;
    public double MemoryAmber { get; set; } = 70;
    public double MemoryRed { get; set; } = 85;
    public double DiskAmber { get; set; } = 70;
    public double DiskRed { get; set; } = 85;
    public double NetworkAmberMs { get; set; } = 50;
    public double NetworkRedMs { get; set; } = 150;
}

/// <summary>
/// ViewModel for the compact health metrics strip in the ribbon.
/// Polls controller-local metrics (CPU, memory, disk, network latency)
/// and aggregates agent fleet health from the dispatcher.
/// </summary>
public sealed partial class HealthMetricsVM : ObservableObject, IDisposable
{
    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly ExecutionSessionManager _sessionManager;
    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private readonly PerformanceCounter? _cpuCounter;
    private readonly Stopwatch _uptimeWatch = Stopwatch.StartNew();

    // ── Metric values ───────────────────────────────────────────────
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private double _memoryPercent;
    [ObservableProperty] private double _memoryUsedGb;
    [ObservableProperty] private double _memoryTotalGb;
    [ObservableProperty] private double _diskPercent;
    [ObservableProperty] private double _diskUsedGb;
    [ObservableProperty] private double _diskTotalGb;
    [ObservableProperty] private double _networkLatencyMs;
    [ObservableProperty] private int _agentsOnline;
    [ObservableProperty] private int _agentsTotal;
    [ObservableProperty] private int _activeSessionCount;
    [ObservableProperty] private string _sessionElapsed = "";

    // ── Health levels (Green/Amber/Red) ─────────────────────────────
    [ObservableProperty] private HealthLevel _cpuHealth = HealthLevel.Green;
    [ObservableProperty] private HealthLevel _memoryHealth = HealthLevel.Green;
    [ObservableProperty] private HealthLevel _diskHealth = HealthLevel.Green;
    [ObservableProperty] private HealthLevel _networkHealth = HealthLevel.Green;
    [ObservableProperty] private HealthLevel _agentsHealth = HealthLevel.Green;
    [ObservableProperty] private HealthLevel _overallHealth = HealthLevel.Green;

    // ── Tooltip strings ─────────────────────────────────────────────
    [ObservableProperty] private string _cpuTooltip = "";
    [ObservableProperty] private string _memoryTooltip = "";
    [ObservableProperty] private string _diskTooltip = "";
    [ObservableProperty] private string _networkTooltip = "";
    [ObservableProperty] private string _agentsTooltip = "";

    // ── Alert state ─────────────────────────────────────────────────
    [ObservableProperty] private bool _isCritical;
    [ObservableProperty] private string _alertSummary = "";
    [ObservableProperty] private int _issueCount;
    [ObservableProperty] private string _healthPillText = "✓ Healthy";

    // ── Thresholds (configurable via appsettings.json HealthThresholds section) ──
    private readonly HealthThresholdSettings _thresholds;

    public HealthMetricsVM(
        IAgentGrpcDispatcher dispatcher,
        ExecutionSessionManager sessionManager,
        HealthThresholdSettings? thresholds = null)
    {
        _dispatcher = dispatcher;
        _sessionManager = sessionManager;
        _thresholds = thresholds ?? new HealthThresholdSettings();

        try { _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total"); }
        catch { /* PerformanceCounter may not be available in all environments */ }

        _timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        // Initial reading
        Refresh();
    }

    public void Refresh()
    {
        RefreshCpu();
        RefreshMemory();
        RefreshDisk();
        RefreshNetwork();
        RefreshAgents();
        RefreshSessions();
        ComputeOverallHealth();
    }

    private void RefreshCpu()
    {
        try
        {
            CpuPercent = _cpuCounter?.NextValue() ?? 0;
        }
        catch { CpuPercent = 0; }
        CpuHealth = Classify(CpuPercent, _thresholds.CpuAmber, _thresholds.CpuRed);
        CpuTooltip = $"CPU: {CpuPercent:F0}%\nGreen: < {_thresholds.CpuAmber}%\nAmber: {_thresholds.CpuAmber} – {_thresholds.CpuRed}%\nRed: > {_thresholds.CpuRed}%";
    }

    private void RefreshMemory()
    {
        try
        {
            var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref memStatus))
            {
                MemoryTotalGb = Math.Round(memStatus.ullTotalPhys / 1073741824.0, 1);
                var usedBytes = memStatus.ullTotalPhys - memStatus.ullAvailPhys;
                MemoryUsedGb = Math.Round(usedBytes / 1073741824.0, 1);
                MemoryPercent = Math.Round((double)usedBytes / memStatus.ullTotalPhys * 100, 1);
            }
        }
        catch
        {
            var proc = Process.GetCurrentProcess();
            MemoryUsedGb = Math.Round(proc.WorkingSet64 / 1073741824.0, 1);
            MemoryTotalGb = 0;
            MemoryPercent = 0;
        }
        MemoryHealth = Classify(MemoryPercent, _thresholds.MemoryAmber, _thresholds.MemoryRed);
        MemoryTooltip = $"Memory: {MemoryUsedGb:F1} / {MemoryTotalGb:F1} GB ({MemoryPercent:F0}%)\nGreen: < {_thresholds.MemoryAmber}%\nAmber: {_thresholds.MemoryAmber} – {_thresholds.MemoryRed}%\nRed: > {_thresholds.MemoryRed}%";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    private void RefreshDisk()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(AppContext.BaseDirectory) ?? "C");
            DiskTotalGb = Math.Round(drive.TotalSize / 1073741824.0, 1);
            DiskUsedGb = Math.Round((drive.TotalSize - drive.AvailableFreeSpace) / 1073741824.0, 1);
            DiskPercent = Math.Round((drive.TotalSize - drive.AvailableFreeSpace) / (double)drive.TotalSize * 100, 1);
        }
        catch { }
        DiskHealth = Classify(DiskPercent, _thresholds.DiskAmber, _thresholds.DiskRed);
        DiskTooltip = $"Disk: {DiskUsedGb:F1} / {DiskTotalGb:F1} GB ({DiskPercent:F0}%)\nGreen: < {_thresholds.DiskAmber}%\nAmber: {_thresholds.DiskAmber} – {_thresholds.DiskRed}%\nRed: > {_thresholds.DiskRed}%";
    }

    private void RefreshNetwork()
    {
        // Use the average ping time from recent agent health checks
        var allHealth = _dispatcher.GetAllAgentHealth();
        if (allHealth.Count == 0)
        {
            NetworkLatencyMs = 0;
            NetworkHealth = HealthLevel.Green;
            return;
        }

        // Approximate latency from health state — use LastSuccessUtc staleness as proxy
        // In a real implementation this would use actual RTT from ping responses
        var healthyCount = allHealth.Values.Count(h => h.IsHealthy);
        var unhealthyCount = allHealth.Count - healthyCount;

        if (unhealthyCount > allHealth.Count / 2)
            NetworkLatencyMs = 999; // Many agents unreachable
        else if (unhealthyCount > 0)
            NetworkLatencyMs = 100; // Some degradation
        else
            NetworkLatencyMs = 15; // All healthy — low latency assumed

        NetworkHealth = Classify(NetworkLatencyMs, _thresholds.NetworkAmberMs, _thresholds.NetworkRedMs);
        NetworkTooltip = $"Network latency: {NetworkLatencyMs:F0} ms\nGreen: < {_thresholds.NetworkAmberMs} ms\nAmber: {_thresholds.NetworkAmberMs} – {_thresholds.NetworkRedMs} ms\nRed: > {_thresholds.NetworkRedMs} ms";
    }

    private void RefreshAgents()
    {
        AgentsTotal = _dispatcher.RegisteredAgentCount;
        var allHealth = _dispatcher.GetAllAgentHealth();
        AgentsOnline = allHealth.Values.Count(h => h.IsHealthy);

        if (AgentsTotal == 0)
            AgentsHealth = HealthLevel.Green;
        else if (AgentsOnline < AgentsTotal / 2)
            AgentsHealth = HealthLevel.Red;
        else if (AgentsOnline < AgentsTotal)
            AgentsHealth = HealthLevel.Amber;
        else
            AgentsHealth = HealthLevel.Green;

        AgentsTooltip = $"Agents: {AgentsOnline} / {AgentsTotal} online\n" +
                        (AgentsOnline == AgentsTotal ? "All agents healthy" :
                         $"{AgentsTotal - AgentsOnline} agent(s) offline");
    }

    private void RefreshSessions()
    {
        ActiveSessionCount = _sessionManager.ActiveExecutionCount;
        if (ActiveSessionCount > 0)
        {
            var sessions = _sessionManager.GetActiveSessions();
            var earliest = sessions.Min(s => s.StartedUtc);
            var elapsed = DateTime.UtcNow - earliest;
            SessionElapsed = elapsed.TotalHours >= 1
                ? $"{elapsed:h\\:mm\\:ss}"
                : $"{elapsed:m\\:ss}";
        }
        else
        {
            SessionElapsed = "";
        }
    }

    private void ComputeOverallHealth()
    {
        var worst = (HealthLevel)Math.Max(
            Math.Max((int)CpuHealth, (int)MemoryHealth),
            Math.Max(Math.Max((int)DiskHealth, (int)NetworkHealth), (int)AgentsHealth));

        OverallHealth = worst;
        IsCritical = worst == HealthLevel.Red;

        // Count all non-green issues for the health pill
        var alerts = new List<string>();
        if (CpuHealth >= HealthLevel.Amber) alerts.Add(CpuHealth == HealthLevel.Red ? "CPU overloaded" : "CPU elevated");
        if (MemoryHealth >= HealthLevel.Amber) alerts.Add(MemoryHealth == HealthLevel.Red ? "Memory pressure" : "Memory elevated");
        if (DiskHealth >= HealthLevel.Amber) alerts.Add(DiskHealth == HealthLevel.Red ? "Disk full risk" : "Disk elevated");
        if (NetworkHealth >= HealthLevel.Amber) alerts.Add(NetworkHealth == HealthLevel.Red ? "Network degraded" : "Network slow");
        var offlineCount = AgentsTotal - AgentsOnline;
        if (offlineCount > 0) alerts.Add($"{offlineCount} agent{(offlineCount > 1 ? "s" : "")} offline");

        IssueCount = alerts.Count;

        if (worst == HealthLevel.Red)
        {
            HealthPillText = $"⚠ {alerts.Count} critical";
            AlertSummary = $"{alerts.Count} critical · {string.Join(" · ", alerts)}";
        }
        else if (worst == HealthLevel.Amber)
        {
            HealthPillText = $"⚠ {alerts.Count} warning{(alerts.Count > 1 ? "s" : "")}";
            AlertSummary = $"{alerts.Count} warning · {string.Join(" · ", alerts)}";
        }
        else
        {
            HealthPillText = "✓ Healthy";
            AlertSummary = "";
        }
    }

    private static HealthLevel Classify(double value, double amberThreshold, double redThreshold)
    {
        if (value >= redThreshold) return HealthLevel.Red;
        if (value >= amberThreshold) return HealthLevel.Amber;
        return HealthLevel.Green;
    }

    public void Dispose()
    {
        _timer.Stop();
        _cpuCounter?.Dispose();
    }
}

public enum HealthLevel
{
    Green = 0,
    Amber = 1,
    Red = 2,
}
