using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TestAgentGrpc.Services;

/// <summary>
/// Collects system resource metrics (CPU, memory, disk) for inclusion
/// in heartbeats and agent snapshots.
/// </summary>
public sealed class SystemMetricsCollector
{
    private readonly ILogger<SystemMetricsCollector> _logger;
    private DateTime _lastCpuCheck = DateTime.MinValue;
    private TimeSpan _lastCpuTotal = TimeSpan.Zero;
    private double _lastCpuPct;

    public SystemMetricsCollector(ILogger<SystemMetricsCollector> logger)
    {
        _logger = logger;
    }

    public ResourceMetrics Collect()
    {
        var metrics = new ResourceMetrics();
        metrics.OsDescription = RuntimeInformation.OSDescription;

        try
        {
            // CPU usage (simple delta of total processor time)
            var now = DateTime.UtcNow;
            var procs = Process.GetProcesses();
            metrics.ActiveProcessCount = procs.Length;

            var totalCpu = TimeSpan.Zero;
            foreach (var p in procs)
            {
                try { totalCpu += p.TotalProcessorTime; } catch { }
                finally { p.Dispose(); }
            }

            if (_lastCpuCheck > DateTime.MinValue)
            {
                var elapsed = (now - _lastCpuCheck).TotalMilliseconds;
                var cpuDelta = (totalCpu - _lastCpuTotal).TotalMilliseconds;
                _lastCpuPct = Math.Min(100, cpuDelta / (elapsed * Environment.ProcessorCount) * 100);
            }
            _lastCpuCheck = now;
            _lastCpuTotal = totalCpu;
            metrics.CpuUsagePct = Math.Round(_lastCpuPct, 1);

            // Memory
            using var self = Process.GetCurrentProcess();
            var gcInfo = GC.GetGCMemoryInfo();
            metrics.MemoryTotalMb = Math.Round((double)gcInfo.TotalAvailableMemoryBytes / (1024 * 1024), 1);
            metrics.MemoryUsedMb  = Math.Round((double)(gcInfo.TotalAvailableMemoryBytes - gcInfo.HighMemoryLoadThresholdBytes) / (1024 * 1024), 1);

            // Fallback: use working set as a simpler metric
            if (metrics.MemoryUsedMb <= 0)
                metrics.MemoryUsedMb = Math.Round((double)self.WorkingSet64 / (1024 * 1024), 1);

            // Disk free space on system drive
            try
            {
                var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
                var drive = new DriveInfo(root);
                metrics.DiskFreeGb = Math.Round((double)drive.AvailableFreeSpace / (1024 * 1024 * 1024), 2);
            }
            catch { }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Metrics collection partial failure");
        }

        return metrics;
    }
}
