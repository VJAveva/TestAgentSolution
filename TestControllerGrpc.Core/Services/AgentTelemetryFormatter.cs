using TestAgentGrpc;

namespace TestControllerGrpc.Services;

/// <summary>
/// Pure formatting logic for agent telemetry metrics.
/// Extracts display values and thresholds from <see cref="ResourceMetrics"/>
/// without any UI/VM dependencies, making it independently testable.
/// </summary>
public static class AgentTelemetryFormatter
{
    /// <summary>Formatted CPU display result.</summary>
    public record CpuResult(string Display, double Percent, string Sub, string Level);

    /// <summary>Formatted memory display result.</summary>
    public record MemoryResult(string Display, double Percent, string Sub, string Level, string RamTotal);

    /// <summary>Formatted disk display result.</summary>
    public record DiskResult(string Display, double Percent, string Sub, string Level, string FreeText);

    /// <summary>Formatted process/network display result.</summary>
    public record ProcessResult(string Display, double HealthPercent, string Sub, string Level);

    public static CpuResult FormatCpu(ResourceMetrics m)
    {
        var pct = Math.Round(m.CpuUsagePct, 0);
        var display = $"{pct:F0}%";
        var sub = $"{m.ActiveProcessCount} processes";
        var level = pct >= 85 ? "Warn" : pct >= 50 ? "Busy" : "Ok";
        return new CpuResult(display, pct, sub, level);
    }

    public static MemoryResult FormatMemory(ResourceMetrics m)
    {
        var memUsedGB = m.MemoryUsedMb / 1024.0;
        var memTotalGB = m.MemoryTotalMb / 1024.0;
        var pct = memTotalGB > 0 ? Math.Round(memUsedGB / memTotalGB * 100, 0) : 0;
        var display = $"{m.MemoryUsedMb:F0} / {m.MemoryTotalMb:F0} MB";
        var sub = memTotalGB > 0 ? $"{100 - pct:F0}% free" : "";
        var level = pct >= 85 ? "Warn" : pct >= 60 ? "Busy" : "Ok";
        var ramTotal = m.MemoryTotalMb > 0 ? $"{memTotalGB:F1} GB" : "";
        return new MemoryResult(display, pct, sub, level, ramTotal);
    }

    public static DiskResult FormatDisk(ResourceMetrics m)
    {
        var display = m.DiskFreeGb > 0 ? $"{m.DiskFreeGb:F1} GB free" : "";
        var pct = 0.0; // Unknown total — treat as ok
        var sub = "available space";
        var level = m.DiskFreeGb < 5 ? "Warn" : m.DiskFreeGb < 20 ? "Busy" : "Ok";
        var freeText = m.DiskFreeGb > 0 ? $"{m.DiskFreeGb:F1} GB free" : "";
        return new DiskResult(display, pct, sub, level, freeText);
    }

    public static ProcessResult FormatProcesses(ResourceMetrics m)
    {
        var display = $"{m.ActiveProcessCount} procs";
        var healthPct = 100.0;
        var sub = "active processes";
        var level = m.ActiveProcessCount > 200 ? "Warn" : "Ok";
        return new ProcessResult(display, healthPct, sub, level);
    }

    /// <summary>Formats agent uptime from a protobuf Timestamp.</summary>
    public static string FormatUptime(Google.Protobuf.WellKnownTypes.Timestamp? agentStarted)
    {
        if (agentStarted == null) return "";
        var uptime = DateTime.UtcNow - agentStarted.ToDateTime();
        return uptime.TotalHours >= 1
            ? $"{(int)uptime.TotalHours}h"
            : $"{(int)uptime.TotalMinutes}m";
    }

    /// <summary>Formats elapsed time for a session.</summary>
    public static string FormatSessionElapsed(DateTime lockedAtUtc)
    {
        return (DateTime.UtcNow - lockedAtUtc).ToString(@"hh\:mm\:ss");
    }
}
