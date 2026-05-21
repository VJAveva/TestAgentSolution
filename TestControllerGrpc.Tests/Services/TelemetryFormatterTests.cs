using TestAgentGrpc;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Tests for <see cref="AgentTelemetryFormatter"/> — validates all formatting
/// logic produces correct display text and level thresholds.
/// </summary>
public class TelemetryFormatterTests
{
    // ═══════════════════════════════════════════════════════════════════
    // CPU
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(0, "Ok")]
    [InlineData(25, "Ok")]
    [InlineData(49, "Ok")]
    [InlineData(50, "Busy")]
    [InlineData(84, "Busy")]
    [InlineData(85, "Warn")]
    [InlineData(100, "Warn")]
    public void FormatCpu_Level_MatchesThresholds(double cpuPct, string expectedLevel)
    {
        var m = new ResourceMetrics { CpuUsagePct = cpuPct, ActiveProcessCount = 10 };
        var result = AgentTelemetryFormatter.FormatCpu(m);
        Assert.Equal(expectedLevel, result.Level);
    }

    [Fact]
    public void FormatCpu_Display_ShowsPercentage()
    {
        var m = new ResourceMetrics { CpuUsagePct = 42.7, ActiveProcessCount = 150 };
        var result = AgentTelemetryFormatter.FormatCpu(m);
        Assert.Equal("43%", result.Display);
        Assert.Equal(43, result.Percent);
        Assert.Equal("150 processes", result.Sub);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Memory
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(4096, 16384, "Ok")]   // 25% used
    [InlineData(9830, 16384, "Busy")] // 60% used
    [InlineData(14000, 16384, "Warn")] // 85%+ used
    public void FormatMemory_Level_MatchesThresholds(double usedMb, double totalMb, string expectedLevel)
    {
        var m = new ResourceMetrics { MemoryUsedMb = usedMb, MemoryTotalMb = totalMb };
        var result = AgentTelemetryFormatter.FormatMemory(m);
        Assert.Equal(expectedLevel, result.Level);
    }

    [Fact]
    public void FormatMemory_Display_ShowsMBValues()
    {
        var m = new ResourceMetrics { MemoryUsedMb = 8192, MemoryTotalMb = 16384 };
        var result = AgentTelemetryFormatter.FormatMemory(m);
        Assert.Contains("8192", result.Display);
        Assert.Contains("16384", result.Display);
        Assert.Equal(50, result.Percent);
        Assert.Equal("50% free", result.Sub);
        Assert.Equal("16.0 GB", result.RamTotal);
    }

    [Fact]
    public void FormatMemory_ZeroTotal_HandlesGracefully()
    {
        var m = new ResourceMetrics { MemoryUsedMb = 0, MemoryTotalMb = 0 };
        var result = AgentTelemetryFormatter.FormatMemory(m);
        Assert.Equal(0, result.Percent);
        Assert.Equal("Ok", result.Level);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Disk
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(3, "Warn")]   // < 5 GB
    [InlineData(10, "Busy")]  // < 20 GB
    [InlineData(50, "Ok")]    // >= 20 GB
    [InlineData(500, "Ok")]
    public void FormatDisk_Level_MatchesThresholds(double freeGb, string expectedLevel)
    {
        var m = new ResourceMetrics { DiskFreeGb = freeGb };
        var result = AgentTelemetryFormatter.FormatDisk(m);
        Assert.Equal(expectedLevel, result.Level);
    }

    [Fact]
    public void FormatDisk_Display_ShowsFreeSpace()
    {
        var m = new ResourceMetrics { DiskFreeGb = 123.4 };
        var result = AgentTelemetryFormatter.FormatDisk(m);
        Assert.Equal("123.4 GB free", result.Display);
        Assert.Equal("123.4 GB free", result.FreeText);
    }

    [Fact]
    public void FormatDisk_ZeroFree_ReturnsEmpty()
    {
        var m = new ResourceMetrics { DiskFreeGb = 0 };
        var result = AgentTelemetryFormatter.FormatDisk(m);
        Assert.Equal("", result.Display);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Processes
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(50, "Ok")]
    [InlineData(200, "Ok")]
    [InlineData(201, "Warn")]
    [InlineData(500, "Warn")]
    public void FormatProcesses_Level_MatchesThresholds(int count, string expectedLevel)
    {
        var m = new ResourceMetrics { ActiveProcessCount = count };
        var result = AgentTelemetryFormatter.FormatProcesses(m);
        Assert.Equal(expectedLevel, result.Level);
    }

    [Fact]
    public void FormatProcesses_Display_ShowsCount()
    {
        var m = new ResourceMetrics { ActiveProcessCount = 142 };
        var result = AgentTelemetryFormatter.FormatProcesses(m);
        Assert.Equal("142 procs", result.Display);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Uptime
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void FormatUptime_Null_ReturnsEmpty()
    {
        Assert.Equal("", AgentTelemetryFormatter.FormatUptime(null));
    }

    [Fact]
    public void FormatUptime_Hours_ShowsHours()
    {
        var started = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(
            DateTime.UtcNow.AddHours(-3));
        var result = AgentTelemetryFormatter.FormatUptime(started);
        Assert.Equal("3h", result);
    }

    [Fact]
    public void FormatUptime_Minutes_ShowsMinutes()
    {
        var started = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(
            DateTime.UtcNow.AddMinutes(-45));
        var result = AgentTelemetryFormatter.FormatUptime(started);
        Assert.Equal("45m", result);
    }
}
