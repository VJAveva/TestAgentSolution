using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.ApiTests.Infrastructure;

/// <summary>
/// In-memory replacement for <see cref="IAgentGrpcDispatcher"/> — the single seam
/// between the execution pipeline and real agent machines. Lets InMemory tests run
/// the REAL executor/session/lock machinery while deterministically controlling the
/// outcome of every command, with no network or remote agents involved.
///
/// Tests tune behavior through <see cref="OnExecute"/> (outcome injection) and
/// <see cref="ExecutionDelay"/> (so a run stays "Running" long enough to observe
/// concurrency / cancellation). Call <see cref="Reset"/> between tests.
/// </summary>
public sealed class FakeAgentDispatcher : IAgentGrpcDispatcher
{
    private readonly Dictionary<string, string> _agents = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string, string, string>? OutputReceived;
    public event Action<string, string>? StatusChanged;

    /// <summary>Decides the result of each command. Defaults to success.</summary>
    public Func<ActionConfig, ActionResult> OnExecute { get; set; } =
        _ => new ActionResult(true, 0, string.Empty);

    /// <summary>Artificial per-command delay (honors cancellation).</summary>
    public TimeSpan ExecutionDelay { get; set; } = TimeSpan.Zero;

    private int _executeCallCount;
    public int ExecuteCallCount => Volatile.Read(ref _executeCallCount);

    public FakeAgentDispatcher()
    {
        _agents["Agent1"] = "http://localhost:15200";
    }

    /// <summary>Restores default success behavior and clears counters/delay.</summary>
    public void Reset()
    {
        OnExecute = _ => new ActionResult(true, 0, string.Empty);
        ExecutionDelay = TimeSpan.Zero;
        Volatile.Write(ref _executeCallCount, 0);
    }

    private async Task<ActionResult> ExecuteAsync(ActionConfig action, CancellationToken ct)
    {
        Interlocked.Increment(ref _executeCallCount);
        StatusChanged?.Invoke(action.Command, "Running");
        if (ExecutionDelay > TimeSpan.Zero)
            await Task.Delay(ExecutionDelay, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var result = OnExecute(action);
        OutputReceived?.Invoke(action.Command, result.ErrorMessage, "stdout");
        StatusChanged?.Invoke(action.Command, result.Success ? "Completed" : "Failed");
        return result;
    }

    public Task<ActionResult> ExecuteRemoteCommandAsync(ActionConfig action, PipelineExecutionContext ctx, CancellationToken ct)
        => ExecuteAsync(action, ct);

    public Task<ActionResult> ExecuteLocalCommandAsync(ActionConfig action, PipelineExecutionContext ctx, CancellationToken ct)
        => ExecuteAsync(action, ct);

    public void RegisterAgent(string agentName, string grpcAddress) => _agents[agentName] = grpcAddress;
    public bool UnregisterAgent(string agentName) => _agents.Remove(agentName);
    public IEnumerable<string> RegisteredAgents => _agents.Keys.ToList();
    public int RegisteredAgentCount => _agents.Count;
    public string? GetAgentAddress(string agentName) => _agents.TryGetValue(agentName, out var a) ? a : null;
    public bool IsAgentExecuting(string agentName) => false;

    public AgentHealthState? GetAgentHealth(string agentName) =>
        _agents.ContainsKey(agentName)
            ? new AgentHealthState { AgentName = agentName, IsHealthy = true, LastSuccessUtc = DateTime.UtcNow }
            : null;

    public IReadOnlyDictionary<string, AgentHealthState> GetAllAgentHealth() =>
        _agents.Keys.ToDictionary(
            k => k,
            k => new AgentHealthState { AgentName = k, IsHealthy = true, LastSuccessUtc = DateTime.UtcNow });

    public Task<bool> PingAsync(string agentName, CancellationToken ct = default) =>
        Task.FromResult(_agents.ContainsKey(agentName));

    public Task<bool> ResetChannelAsync(string agentName) => Task.FromResult(true);

    public Task<List<DiagnosticStep>> DiagnoseAgentAsync(string agentName, CancellationToken ct = default) =>
        Task.FromResult(new List<DiagnosticStep> { new("Fake", true, "ok") });

    public Task<(TestAgentGrpc.AgentSnapshot? Snapshot, string? Error)> TestConnectionAsync(string agentName, CancellationToken ct = default) =>
        Task.FromResult<(TestAgentGrpc.AgentSnapshot?, string?)>((null, "fake dispatcher: no snapshot"));

    public void Dispose() { }
}
