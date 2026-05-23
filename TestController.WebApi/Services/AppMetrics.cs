using System.Diagnostics.Metrics;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Services;

/// <summary>
/// Custom application metrics exposed via OpenTelemetry.
/// Tracks execution counts, active sessions, agent registry size, and failure rates.
/// </summary>
public sealed class AppMetrics
{
    public const string MeterName = "TestController.WebApi";

    private readonly Counter<long> _executionsStarted;
    private readonly Counter<long> _executionsCompleted;
    private readonly Counter<long> _executionsFailed;
    private readonly ObservableGauge<int> _activeSessionsGauge;
    private readonly ObservableGauge<int> _registeredAgentsGauge;

    private readonly ExecutionSessionManager _sessions;
    private readonly AgentRegistry _registry;

    public AppMetrics(IMeterFactory meterFactory, ExecutionSessionManager sessions, AgentRegistry registry)
    {
        var meter = meterFactory.Create(MeterName);
        _sessions = sessions;
        _registry = registry;

        _executionsStarted = meter.CreateCounter<long>(
            "testcontroller.executions.started",
            description: "Total number of execution sessions started");

        _executionsCompleted = meter.CreateCounter<long>(
            "testcontroller.executions.completed",
            description: "Total number of execution sessions completed successfully");

        _executionsFailed = meter.CreateCounter<long>(
            "testcontroller.executions.failed",
            description: "Total number of execution sessions that failed");

        _activeSessionsGauge = meter.CreateObservableGauge(
            "testcontroller.sessions.active",
            () => _sessions.GetActiveSessions().Count,
            description: "Number of currently active execution sessions");

        _registeredAgentsGauge = meter.CreateObservableGauge(
            "testcontroller.agents.registered",
            () => _registry.GetAll().Count,
            description: "Number of registered agents");
    }

    public void RecordExecutionStarted() => _executionsStarted.Add(1);
    public void RecordExecutionCompleted() => _executionsCompleted.Add(1);
    public void RecordExecutionFailed() => _executionsFailed.Add(1);
}
