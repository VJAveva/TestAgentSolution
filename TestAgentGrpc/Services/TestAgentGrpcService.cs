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
    private readonly EnhancedCommandPolicyEvaluator _policyEvaluator;
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
        EnhancedCommandPolicyEvaluator policyEvaluator,
        Microsoft.Extensions.Options.IOptions<AgentSettings> settings,
        ILogger<TestAgentGrpcService> logger)
    {
        _executor        = executor;
        _broadcaster     = broadcaster;
        _tracker         = tracker;
        _metrics         = metrics;
        _audit           = audit;
        _healthMonitor   = healthMonitor;
        _policyEvaluator = policyEvaluator;
        _settings        = settings.Value;
        _logger          = logger;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Legacy-compatible RPCs (match the original ITestAgentSvc contract)
    // ═══════════════════════════════════════════════════════════════════

    public override Task<StateReply> GetState(Empty request, ServerCallContext context)
    {
        // Include hostname in response headers for auto-discovery
        context.ResponseTrailers.Add("x-agent-hostname", Environment.MachineName);
        return Task.FromResult(new StateReply { State = _executor.CurrentState });
    }

    public override Task<RunCommandReply> RunCommand(RunCommandRequest request, ServerCallContext context)
    {
        _logger.LogInformation("RunCommand: {CmdLine}",
            SecurityRedactor.RedactCommandLine(request.Command, request.Arguments));

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

    public override Task<Empty> ForceReady(Empty request, ServerCallContext context)
    {
        _logger.LogWarning("ForceReady requested — forcibly resetting agent state");
        _executor.ForceReady();
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
        var correlationId = context.RequestHeaders
            .FirstOrDefault(h => h.Key == "x-correlation-id")?.Value ?? "";

        _logger.LogInformation("[{CorrelationId}] RunCommandStreamed: {CmdLine}",
            correlationId,
            SecurityRedactor.RedactCommandLine(request.Command, request.Arguments));

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
            externalCt: context.CancellationToken);

        if (!accepted || reader is null)
        {
            _audit.Log("CommandRejected", severity: "Warning",
                source: context.Peer, command: request.Command, arguments: request.Arguments,
                detail: "Agent is busy (streamed)", correlationId: correlationId);

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
            credentials: string.IsNullOrEmpty(request.UserName) ? null : request.UserName,
            correlationId: correlationId);

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
        reply.Records.AddRange(_tracker.GetHistory(
            max: request.MaxResults,
            filterCommand: string.IsNullOrEmpty(request.FilterCommand) ? null : request.FilterCommand
        ).Select(r =>
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

    /// <summary>
    /// Returns a one-shot snapshot of the agent's current state, activity,
    /// resource metrics, and execution counters.
    /// </summary>
    public override Task<AgentSnapshot> GetAgentSnapshot(Empty request, ServerCallContext context)
    {
        var snapshot = new AgentSnapshot
        {
            AgentName          = _settings.AgentName,
            State              = _executor.CurrentState,
            CurrentActivity    = _executor.Activity,
            CurrentExecutionId = _executor.CurrentExecutionId ?? "",
            CurrentCommand     = _executor.CurrentCommand ?? "",
            ExecutionsCompleted = _tracker.CompletedCount,
            ExecutionsFailed    = _tracker.FailedCount,
            AgentStarted       = Timestamp.FromDateTime(_agentStartedUtc),
            Metrics            = _metrics.Collect(),
            AgentVersion       = typeof(TestAgentGrpcService).Assembly.GetName().Version?.ToString() ?? "0.0.0",
        };
        snapshot.Capabilities.AddRange(AgentCapabilities);

        if (_executor.ExecutionStartedUtc is { } started)
            snapshot.ExecutionStarted = Timestamp.FromDateTime(DateTime.SpecifyKind(started, DateTimeKind.Utc));

        return Task.FromResult(snapshot);
    }

    private static readonly string[] AgentCapabilities =
    [
        "ForceReady",
        "TerminateExecution",
        "StreamedOutput",
        "ExecutionHistory",
        "AuditLog",
        "ConnectionHealth",
        "CommandPolicy",
    ];

    /// <summary>
    /// Returns audit log entries matching the requested date range and filters.
    /// </summary>
    public override Task<AuditLogReply> GetAuditLog(AuditLogRequest request, ServerCallContext context)
    {
        var entries = _audit.ReadEntries(
            request.FromDate, request.ToDate,
            request.EventFilter,
            request.MaxEntries > 0 ? request.MaxEntries : 500);

        var reply = new AuditLogReply();
        reply.Entries.AddRange(entries.Select(e => new AuditLogEntry
        {
            Timestamp   = e.Timestamp.ToString("O"),
            Event       = e.Event,
            Severity    = e.Severity,
            ExecutionId = e.ExecutionId ?? "",
            Source      = e.Source ?? "",
            Command     = e.Command ?? "",
            Detail      = e.Detail ?? "",
            ExitCode    = e.ExitCode ?? 0,
            DurationMs  = e.DurationMs ?? 0,
        }));

        return Task.FromResult(reply);
    }

    /// <summary>
    /// Returns connection health information for the agent's link to the controller.
    /// </summary>
    public override Task<ConnectionHealthReply> GetConnectionHealth(
        ConnectionHealthRequest request, ServerCallContext context)
    {
        var reply = new ConnectionHealthReply
        {
            ControllerName    = _healthMonitor.ControllerName ?? "",
            ControllerAddress = _healthMonitor.ControllerAddress ?? "",
            IsConnected       = _healthMonitor.IsConnected,
            ConsecutiveFailures = _healthMonitor.ConsecutiveFailures,
            TotalHeartbeatsSent   = _healthMonitor.TotalHeartbeatsSent,
            TotalHeartbeatsFailed = _healthMonitor.TotalHeartbeatsFailed,
            CurrentSuccessStreak  = _healthMonitor.CurrentSuccessStreak,
            EventStreamActive     = _healthMonitor.IsConnected,
            LastDisconnectReason  = _healthMonitor.LastError ?? "",
        };

        if (_healthMonitor.LastSuccessfulHeartbeat is { } lastOk)
            reply.LastSuccessfulHeartbeat = Timestamp.FromDateTime(DateTime.SpecifyKind(lastOk, DateTimeKind.Utc));

        if (_healthMonitor.LastFailedHeartbeat is { } lastFail)
            reply.LastFailedHeartbeat = Timestamp.FromDateTime(DateTime.SpecifyKind(lastFail, DateTimeKind.Utc));

        if (_healthMonitor.RegistrationTimestamp is { } regTs)
            reply.RegistrationTimestamp = Timestamp.FromDateTime(DateTime.SpecifyKind(regTs, DateTimeKind.Utc));

        if (_healthMonitor.DowntimeDuration is { } downtime)
            reply.LastDowntimeDuration = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(downtime);

        return Task.FromResult(reply);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Command Policy Management
    // ═══════════════════════════════════════════════════════════════════

    public override Task<CommandPolicyReply> ReloadCommandPolicy(Empty request, ServerCallContext context)
    {
        _logger.LogInformation("ReloadCommandPolicy RPC invoked");
        _policyEvaluator.Reload();

        var reply = new CommandPolicyReply
        {
            Success = true,
            Mode = _policyEvaluator.IsEnforced ? "Enforce" : _policyEvaluator.IsAuditOnly ? "AuditOnly" : "Disabled",
            Message = "Command policy reloaded successfully"
        };
        return Task.FromResult(reply);
    }
}
