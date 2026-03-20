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
///   • <c>GetAuditLog</c>          — query persistent audit logs
/// </summary>
public sealed class TestAgentGrpcService : TestAgentService.TestAgentServiceBase
{
    private readonly CommandExecutor _executor;
    private readonly EventBroadcaster _broadcaster;
    private readonly ExecutionTracker _tracker;
    private readonly SystemMetricsCollector _metrics;
    private readonly AuditLogger _audit;
    private readonly ConnectionHealthMonitor _healthMonitor;
    private readonly AgentSettings _settings;
    private readonly ILogger<TestAgentGrpcService> _logger;
    private readonly DateTime _agentStartedUtc = DateTime.UtcNow;

    public TestAgentGrpcService(
        CommandExecutor executor,
        EventBroadcaster broadcaster,
        ExecutionTracker tracker,
        SystemMetricsCollector metrics,
        AuditLogger audit,
        ConnectionHealthMonitor healthMonitor,
        Microsoft.Extensions.Options.IOptions<AgentSettings> settings,
        ILogger<TestAgentGrpcService> logger)
    {
        _executor      = executor;
        _broadcaster   = broadcaster;
        _tracker       = tracker;
        _metrics       = metrics;
        _audit         = audit;
        _healthMonitor = healthMonitor;
        _settings      = settings.Value;
        _logger        = logger;
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
            _audit.Log("CommandRejected", severity: "Warning",
                source: context.Peer, command: request.Command, arguments: request.Arguments,
                detail: "Agent is busy executing another command");

            return Task.FromResult(new RunCommandReply
            {
                Accepted = false,
                Message = "Agent is busy executing another command.",
            });
        }

        _audit.Log("CommandReceived", source: context.Peer,
            executionId: execId, command: request.Command, arguments: request.Arguments,
            credentials: string.IsNullOrEmpty(request.UserName) ? null : request.UserName);

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

        // Convert timeout from seconds to milliseconds (0 = no timeout)
        var timeoutMs = request.TimeoutSeconds > 0 ? request.TimeoutSeconds * 1000 : 0;

        var (accepted, execId, reader) = _executor.RunCommandStreamed(
            request.Command, request.Arguments, request.IsReboot,
            timeoutMs: timeoutMs,
            executionId: request.ExecutionId,
            userName: request.UserName, password: request.Password,
            completionCheckCommand: request.CompletionCheckCommand,
            completionPollIntervalSeconds: request.CompletionPollIntervalSeconds > 0
                ? request.CompletionPollIntervalSeconds : 30,
            enableInstallLog: request.EnableInstallLog,
            installLogPollSeconds: request.InstallLogPollSeconds > 0 ? request.InstallLogPollSeconds : 5,
            installLogRoot: request.InstallLogRoot,
            externalCt: context.CancellationToken);

        if (!accepted || reader is null)
        {
            _audit.Log("CommandRejected", severity: "Warning",
                source: context.Peer, command: request.Command, arguments: request.Arguments,
                detail: "Agent is busy (streamed)");

            // Send a single FAILED event and close
            await responseStream.WriteAsync(new ExecutionEvent
            {
                ExecutionId = request.ExecutionId ?? "rejected",
                AgentName   = _settings.AgentName,
                Timestamp   = Timestamp.FromDateTime(DateTime.UtcNow),
                EventType   = ExecutionEventType.EventFailed,
                ErrorMessage = "Agent is busy.",
                Detail       = "Command rejected — another execution is in progress.",
            });
            return;
        }

        _audit.Log("CommandReceived", source: context.Peer,
            executionId: execId, command: request.Command, arguments: request.Arguments,
            credentials: string.IsNullOrEmpty(request.UserName) ? null : request.UserName);

        // Stream events until execution completes or client disconnects
        try
        {
            await foreach (var evt in reader.ReadAllAsync(context.CancellationToken))
            {
                await responseStream.WriteAsync(evt);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — ExecuteAsync will handle cancellation
            // via the externalCt we passed through. Just drain the reader
            // so the channel completes cleanly.
            _logger.LogInformation("RunCommandStreamed client disconnected for {Id}", execId);
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
        var reply = new ExecutionHistoryReply();
        reply.Records.AddRange(_tracker.GetHistory().Select(r =>
        {
            var rec = new ExecutionRecord
            {
                ExecutionId  = r.ExecutionId,
                Command      = r.Command,
                Arguments    = r.Arguments,
                ExitCode     = r.ExitCode,
                Started      = r.Started,
                Finished     = r.Finished,
                ErrorMessage = r.ErrorMessage,
                Outcome      = r.Outcome,
            };
            rec.StdoutLines.AddRange(r.StdoutLines);
            rec.StderrLines.AddRange(r.StderrLines);
            return rec;
        }));
        return Task.FromResult(reply);
    }
}
