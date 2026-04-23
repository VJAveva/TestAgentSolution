using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Services;

/// <summary>
/// Adapts <see cref="AgentRegistry"/> + <see cref="AgentGrpcClientManager"/> to the
/// <see cref="IAgentGrpcDispatcher"/> interface so that the shared API controllers
/// (which depend on IAgentGrpcDispatcher) can work in standalone WebApi mode.
///
/// This adapter provides agent registry and health queries. Actual command execution
/// is not supported in standalone mode — use the WPF-hosted controller for that.
/// </summary>
public sealed class StandaloneAgentDispatcher : IAgentGrpcDispatcher
{
    private readonly AgentRegistry _registry;

    public event Action<string, string, string>? OutputReceived;
    public event Action<string, string>? StatusChanged;

    public StandaloneAgentDispatcher(AgentRegistry registry)
    {
        _registry = registry;
    }

    public void RegisterAgent(string agentName, string grpcAddress)
        => _registry.Register(agentName, grpcAddress);

    public bool UnregisterAgent(string agentName)
        => _registry.Unregister(agentName);

    public Task<(TestAgentGrpc.AgentSnapshot? Snapshot, string? Error)> TestConnectionAsync(
        string agentName, CancellationToken ct = default)
        => Task.FromResult<(TestAgentGrpc.AgentSnapshot?, string?)>((null, "Not supported in standalone mode"));

    public Task<List<DiagnosticStep>> DiagnoseAgentAsync(string agentName, CancellationToken ct = default)
        => Task.FromResult(new List<DiagnosticStep>
        {
            new("Standalone Mode", false, "Full diagnostics require the WPF-hosted controller")
        });

    public Task<bool> PingAsync(string agentName, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<ActionResult> ExecuteRemoteCommandAsync(
        ActionConfig action, PipelineExecutionContext ctx, CancellationToken ct)
        => Task.FromResult(new ActionResult(false, -1, "Remote execution not supported in standalone WebApi mode"));

    public Task<ActionResult> ExecuteLocalCommandAsync(
        ActionConfig action, PipelineExecutionContext ctx, CancellationToken ct)
        => Task.FromResult(new ActionResult(false, -1, "Local execution not supported in standalone WebApi mode"));

    public IEnumerable<string> RegisteredAgents
        => _registry.GetAll().Select(a => a.Name);

    public int RegisteredAgentCount
        => _registry.GetAll().Count;

    public string? GetAgentAddress(string agentName)
        => _registry.TryGet(agentName, out var e) ? e.Address : null;

    public AgentHealthState? GetAgentHealth(string agentName)
        => _registry.TryGet(agentName, out _)
            ? new AgentHealthState { AgentName = agentName, IsHealthy = true }
            : null;

    public IReadOnlyDictionary<string, AgentHealthState> GetAllAgentHealth()
        => _registry.GetAll().ToDictionary(
            a => a.Name,
            a => new AgentHealthState { AgentName = a.Name, IsHealthy = true },
            StringComparer.OrdinalIgnoreCase);

    public void Dispose() { }
}
