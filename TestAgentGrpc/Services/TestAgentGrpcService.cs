using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace TestAgentGrpc.Services;

/// <summary>
/// gRPC server-side implementation of <see cref="TestAgentService"/>.
///
/// Provides both the legacy fire-and-forget RPCs (backward compatible with
/// the controller) and the new real-time streaming RPCs that enable:
///   • <c>RunCommandStreamed</c>  — stream every stdout/stderr line as it happens
///   • <c>SubscribeAgentEvents</c> — firehose of ALL agent events
///   • <c>GetExecutionHistory</c>  — query past runs
///   • <c>GetAgentSnapshot</c>     — one-shot current state + metrics
/// </summary>
public sealed class TestAgentGrpcService : TestAgentService.TestAgentServiceBase
{
    private readonly CommandExecutor _executor;
    private readonly EventBroadcaster _broadcaster;
    private readonly ExecutionTracker _tracker;
    private readonly SystemMetricsCollector _metrics;
    private readonly AgentSettings _settings;
    private readonly ILogger<TestAgentGrpcService> _logger;
    private readonly DateTime _agentStartedUtc = DateTime.UtcNow;

    public TestAgentGrpcService(
        CommandExecutor executor,
        EventBroadcaster broadcaster,
        ExecutionTracker tracker,
        SystemMetricsCollector metrics,
        Microsoft.Extensions.Options.IOptions<AgentSettings> settings,
        ILogger<TestAgentGrpcService> logger)
    {
        _executor    = executor;
        _broadcaster = broadcaster;
        _tracker     = tracker;
        _metrics     = metrics;
        _settings    = settings.Value;
        _logger      = logger;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Legacy-compatible RPCs (match the original ITestAgentSvc contract)
    // ═══════════════════════════════════════════════════════════════════

    public override Task<StateReply> GetState(Empty request, ServerCallContext context)
    {
        return Task.FromResult(new StateReply { State = _executor.CurrentState });
    }

    public override Task<RunCommandReply> RunCommand(RunCommandRequest request, ServerCallContext context)
    {
        _logger.LogInformation("RunCommand: {Cmd} {Args}", request.Command, request.Arguments);

        var (accepted, execId) = _executor.RunCommand(
            request.Command, request.Arguments, request.IsReboot, request.ExecutionId,
            userName: request.UserName, password: request.Password);

        if (!accepted)
        {
            return Task.FromResult(new RunCommandReply
            {
                Accepted = false,
                Message = "Agent is busy executing another command.",
            });
        }

        return Task.FromResult(new RunCommandReply
        {
            Accepted = true,
            ExecutionId = execId,
            Message = "Command accepted.",
        });
    }

    public override Task<ExitCodeReply> GetLastExitCode(Empty request, ServerCallContext context)
    {
        return Task.FromResult(new ExitCodeReply { ExitCode = _executor.LastExitCode });
    }

    public override Task<ErrorReply> GetLastError(Empty request, ServerCallContext context)
    {
        return Task.FromResult(new ErrorReply { ErrorMessage = _executor.LastError });
    }

    public override Task<Empty> TerminateExecution(Empty request, ServerCallContext context)
    {
        _logger.LogWarning("TerminateExecution requested");
        _executor.TerminateExecution();
        return Task.FromResult(new Empty());
    }

    // ═══════════════════════════════════════════════════════════════════
    // NEW: Real-time execution monitoring RPCs
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Executes a command and streams every event (STARTED → STDOUT lines →
    /// STDERR lines → COMPLETED/FAILED) to the caller in real time.
    ///
    /// The stream closes when the process exits.
    /// </summary>
    public override async Task RunCommandStreamed(
        RunCommandRequest request,
        IServerStreamWriter<ExecutionEvent> responseStream,
        ServerCallContext context)
    {
        _logger.LogInformation("RunCommandStreamed: {Cmd} {Args}", request.Command, request.Arguments);

        var (accepted, execId, reader) = _executor.RunCommandStreamed(
            request.Command, request.Arguments, request.IsReboot, request.ExecutionId,
            userName: request.UserName, password: request.Password);

        if (!accepted || reader is null)
        {
            // Send a single FAILED event and close
            await responseStream.WriteAsync(new ExecutionEvent
            {
                ExecutionId = request.ExecutionId ?? "rejected",
                AgentName   = _settings.GetResolvedEndpoint(),
                Timestamp   = Timestamp.FromDateTime(DateTime.UtcNow),
                EventType   = ExecutionEventType.EventFailed,
                ErrorMessage = "Agent is busy.",
                Detail       = "Command rejected — another execution is in progress.",
            });
            return;
        }

        // Stream events until execution completes
        await foreach (var evt in reader.ReadAllAsync(context.CancellationToken))
        {
            await responseStream.WriteAsync(evt);
        }
    }

    /// <summary>
    /// Firehose subscription: streams ALL agent events (execution output,
    /// state changes, heartbeats) until the caller disconnects.
    ///
    /// The controller can open one of these per agent node to build a
    /// centralised real-time dashboard.
    /// </summary>
    public override async Task SubscribeAgentEvents(
        Empty request,
        IServerStreamWriter<ExecutionEvent> responseStream,
        ServerCallContext context)
    {
        _logger.LogInformation("Client subscribed to agent event stream");

        var (reader, subscription) = _broadcaster.Subscribe();
        using (subscription)
        {
            try
            {
                await foreach (var evt in reader.ReadAllAsync(context.CancellationToken))
                {
                    await responseStream.WriteAsync(evt);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Agent event subscription ended (client disconnected)");
            }
        }
    }

    /// <summary>
    /// Returns a list of past executions with full stdout/stderr capture.
    /// </summary>
    public override Task<ExecutionHistoryReply> GetExecutionHistory(
        ExecutionHistoryRequest request, ServerCallContext context)
    {
        var records = _tracker.GetHistory(request.MaxResults, request.FilterCommand);
        var reply = new ExecutionHistoryReply();
        reply.Records.AddRange(records);
        return Task.FromResult(reply);
    }

    /// <summary>
    /// Returns a single snapshot of the agent's current state, running
    /// command, and resource metrics. Useful for dashboard polling as a
    /// lighter alternative to the streaming subscription.
    /// </summary>
    public override Task<AgentSnapshot> GetAgentSnapshot(Empty request, ServerCallContext context)
    {
        var snapshot = new AgentSnapshot
        {
            AgentName            = _settings.GetResolvedEndpoint(),
            State                = _executor.CurrentState,
            CurrentActivity      = _executor.Activity,
            CurrentExecutionId   = _executor.CurrentExecutionId ?? "",
            CurrentCommand       = _executor.CurrentCommand ?? "",
            Metrics              = _metrics.Collect(),
            ExecutionsCompleted  = _tracker.CompletedCount,
            ExecutionsFailed     = _tracker.FailedCount,
            AgentStarted         = Timestamp.FromDateTime(_agentStartedUtc),
        };

        if (_executor.ExecutionStartedUtc.HasValue)
            snapshot.ExecutionStarted = Timestamp.FromDateTime(_executor.ExecutionStartedUtc.Value);

        return Task.FromResult(snapshot);
    }
}
