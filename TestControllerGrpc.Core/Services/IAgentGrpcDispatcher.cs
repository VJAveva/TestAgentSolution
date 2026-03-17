using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Dispatches commands to agent nodes via gRPC.
/// Maintains a pool of channels keyed by agent name ? address.
/// Supports command execution with timeout, polling, and reboot handling.
/// </summary>
public interface IAgentGrpcDispatcher : IDisposable
{
    /// <summary>Raised when execution output arrives (agentName, line, kind).</summary>
    event Action<string, string, string>? OutputReceived;

    /// <summary>Raised when execution state changes (agentName, status).</summary>
    event Action<string, string>? StatusChanged;

    /// <summary>Registers an agent name ? gRPC address mapping.</summary>
    void RegisterAgent(string agentName, string grpcAddress);

    /// <summary>Removes a registered agent and disposes its gRPC channel.</summary>
    bool UnregisterAgent(string agentName);

    /// <summary>
    /// Tests connectivity to an agent by calling GetAgentSnapshot.
    /// Returns the snapshot on success, or null + error message on failure.
    /// </summary>
    Task<(TestAgentGrpc.AgentSnapshot? Snapshot, string? Error)> TestConnectionAsync(string agentName, CancellationToken ct = default);

    /// <summary>
    /// Performs a comprehensive multi-step diagnostic of agent connectivity.
    /// </summary>
    Task<List<DiagnosticStep>> DiagnoseAgentAsync(string agentName, CancellationToken ct = default);

    /// <summary>Quick ping — checks if GetState responds within 3 seconds.</summary>
    Task<bool> PingAsync(string agentName, CancellationToken ct = default);

    /// <summary>Executes a RunRemoteCommand on the specified agent.</summary>
    Task<ActionResult> ExecuteRemoteCommandAsync(ActionConfig action, PipelineExecutionContext ctx, CancellationToken ct);

    /// <summary>Executes a local RunCommand on the controller machine.</summary>
    Task<ActionResult> ExecuteLocalCommandAsync(ActionConfig action, PipelineExecutionContext ctx, CancellationToken ct);

    /// <summary>Gets the names of all registered agents.</summary>
    IEnumerable<string> RegisteredAgents { get; }

    /// <summary>Gets the address of a registered agent.</summary>
    string? GetAgentAddress(string agentName);

    /// <summary>Gets the current health state for a registered agent.</summary>
    AgentHealthState? GetAgentHealth(string agentName);

    /// <summary>Gets the health states for all registered agents.</summary>
    IReadOnlyDictionary<string, AgentHealthState> GetAllAgentHealth();
}
