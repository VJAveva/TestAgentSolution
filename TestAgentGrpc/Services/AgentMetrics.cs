using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TestAgentGrpc.Services;

/// <summary>
/// Custom agent metrics exposed via OpenTelemetry / Prometheus.
/// Mirrors the WebApi <c>AppMetrics</c> pattern so all hosts ship comparable
/// telemetry. Uses observable instruments that pull live state from the agent's
/// existing trackers — no wiring into the execution hot path required.
/// </summary>
public sealed class AgentMetrics
{
    public const string MeterName = "TestAgent";

    private static readonly DateTime StartedUtc =
        Process.GetCurrentProcess().StartTime.ToUniversalTime();

    public AgentMetrics(IMeterFactory meterFactory, ExecutionTracker tracker,
        CommandExecutor executor, SystemMetricsCollector systemMetrics)
    {
        var meter = meterFactory.Create(MeterName);

        meter.CreateObservableCounter(
            "testagent.executions.completed",
            () => (long)tracker.CompletedCount,
            description: "Total commands the agent has completed successfully");

        meter.CreateObservableCounter(
            "testagent.executions.failed",
            () => (long)tracker.FailedCount,
            description: "Total commands the agent has failed");

        meter.CreateObservableGauge(
            "testagent.busy",
            () => executor.CurrentState == AgentState.Running ? 1 : 0,
            description: "1 when the agent is executing a command, otherwise 0");

        meter.CreateObservableGauge(
            "testagent.uptime.seconds",
            () => (DateTime.UtcNow - StartedUtc).TotalSeconds,
            unit: "s",
            description: "Agent process uptime in seconds");

        meter.CreateObservableGauge(
            "testagent.system.cpu.percent",
            () => systemMetrics.Collect().CpuUsagePct,
            unit: "%",
            description: "Host CPU usage percent");

        meter.CreateObservableGauge(
            "testagent.system.memory.used.mb",
            () => systemMetrics.Collect().MemoryUsedMb,
            unit: "MB",
            description: "Host memory used in megabytes");

        meter.CreateObservableGauge(
            "testagent.system.disk.free.gb",
            () => systemMetrics.Collect().DiskFreeGb,
            unit: "GB",
            description: "Free disk space on the system drive in gigabytes");
    }
}
