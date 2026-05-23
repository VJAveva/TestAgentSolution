using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels;

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
    [ObservableProperty] private HealthLevel _overallHealth = HealthLevel.Green;

    // ── Alert state ─────────────────────────────────────────────────
    [ObservableProperty] private bool _isCritical;
    [ObservableProperty] private string _alertSummary = "";

    // ── Thresholds ──────────────────────────────────────────────────
    private const double CpuAmber = 60, CpuRed = 85;
    private const double MemAmber = 60, MemRed = 85;
    private const double DiskAmber = 70, DiskRed = 90;
    private const double NetAmber = 50, NetRed = 200;

    public HealthMetricsVM(
        IAgentGrpcDispatcher dispatcher,
        ExecutionSessionManager sessionManager)
    {
        _dispatcher = dispatcher;
        _sessionManager = sessionManager;

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
        CpuHealth = Classify(CpuPercent, CpuAmber, CpuRed);
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
        MemoryHealth = Classify(MemoryPercent, MemAmber, MemRed);
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
        DiskHealth = Classify(DiskPercent, DiskAmber, DiskRed);
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

        NetworkHealth = Classify(NetworkLatencyMs, NetAmber, NetRed);
    }

    private void RefreshAgents()
    {
        AgentsTotal = _dispatcher.RegisteredAgentCount;
        var allHealth = _dispatcher.GetAllAgentHealth();
        AgentsOnline = allHealth.Values.Count(h => h.IsHealthy);
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
            Math.Max((int)DiskHealth, (int)NetworkHealth));

        // Also consider agent availability
        if (AgentsTotal > 0 && AgentsOnline < AgentsTotal / 2)
            worst = HealthLevel.Red;
        else if (AgentsTotal > 0 && AgentsOnline < AgentsTotal)
            worst = (HealthLevel)Math.Max((int)worst, (int)HealthLevel.Amber);

        OverallHealth = worst;
        IsCritical = worst == HealthLevel.Red;

        if (IsCritical)
        {
            var alerts = new List<string>();
            if (CpuHealth == HealthLevel.Red) alerts.Add("CPU overloaded");
            if (MemoryHealth == HealthLevel.Red) alerts.Add("Memory pressure");
            if (DiskHealth == HealthLevel.Red) alerts.Add("Disk full risk");
            if (NetworkHealth == HealthLevel.Red) alerts.Add("Network degraded");
            var offlineCount = AgentsTotal - AgentsOnline;
            if (offlineCount > 0) alerts.Add($"{offlineCount} agent{(offlineCount > 1 ? "s" : "")} offline");
            AlertSummary = $"{alerts.Count} critical · {string.Join(" · ", alerts)}";
        }
        else
        {
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
