namespace TestAgentGrpc;

/// <summary>
/// Strongly-typed settings from appsettings.json → "AgentSettings".
/// </summary>
public sealed class AgentSettings
{
    /// <summary>
    /// Friendly name used to identify this agent in the Controller's
    /// WatchList XML (e.g. "AppSerCI1").  Defaults to the machine name.
    /// </summary>
    public string AgentName { get; set; } = Environment.MachineName;

    public int GrpcPort { get; set; } = 5200;
    public string ControllerAddress { get; set; } = "http://localhost:5100";
    public string? AgentEndpoint { get; set; }

    // Registration
    public int RegistrationRetryCount { get; set; } = 3;
    public int RegistrationRetryIntervalSeconds { get; set; } = 30;

    // Heartbeat
    public int HeartbeatIntervalSeconds { get; set; } = 15;

    // Execution history
    public int MaxExecutionHistoryCount { get; set; } = 200;
    public int MaxOutputLinesPerExecution { get; set; } = 5000;

    // Metrics
    public bool CollectSystemMetrics { get; set; } = true;

    public string GetResolvedEndpoint() =>
        AgentEndpoint ?? $"http://{Environment.MachineName}:{GrpcPort}";
}
