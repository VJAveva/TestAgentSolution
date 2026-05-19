using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using TestAgentGrpc;
using TestController.WebApi.Services;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Endpoints;

public static class AgentEndpoints
{
    public static RouteGroupBuilder MapAgentEndpoints(this RouteGroupBuilder group)
    {
        // These endpoints extend the shared AgentsController with standalone-specific features.
        // Common endpoint (GET /) is provided by the shared library.
        group.MapPost("/register", RegisterAgent);
        group.MapDelete("/{name}", UnregisterAgent);
        group.MapPost("/{name}/test", TestAgent);
        group.MapPost("/{name}/diagnose", DiagnoseAgent);
        group.MapGet("/{name}/snapshot", GetAgentSnapshot);
        group.MapGet("/{name}/health", GetAgentHealth);
        group.MapGet("/{name}/history", GetAgentHistory);
        group.MapGet("/{name}/audit", GetAgentAudit);
        group.MapGet("/fleet", GetFleet);
        group.MapGet("/{name}/details", GetDetails);
        group.MapGet("/{name}/telemetry", GetTelemetry);
        group.MapPut("/{name}", UpdateAgent);
        return group;
    }

    /// <summary>GET /api/agents � list all registered agents with status.</summary>
    private static IResult ListAgents(AgentRegistry registry)
    {
        var agents = registry.GetAll().Select(a => new
        {
            a.Name,
            a.Address,
            a.Status,
            a.LastStatusDetail,
            a.LastCheckedUtc
        });
        return Results.Ok(agents);
    }

    /// <summary>POST /api/agents/register — register a new agent and verify connectivity.</summary>
    private static async Task<IResult> RegisterAgent(
        AgentRegisterRequest req,
        AgentRegistry registry,
        AgentGrpcClientManager grpcManager,
        IRealtimeNotifier notifier)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Address))
            return Results.BadRequest("Name and Address are required.");

        registry.Register(req.Name, req.Address);

        // Attempt connectivity check — agent stays registered regardless of outcome
        string status;
        string? detail = null;
        try
        {
            var client = grpcManager.GetClient(req.Address);
            var state = await client.GetStateAsync(new Empty());
            status = state.State.ToString();
        }
        catch (RpcException ex)
        {
            status = "Unhealthy";
            detail = $"Registration succeeded but agent is unreachable: {ex.Status.Detail}";
        }
        catch (Exception ex)
        {
            status = "Unhealthy";
            detail = $"Registration succeeded but connectivity check failed: {ex.Message}";
        }

        registry.UpdateStatus(req.Name, status, detail);
        await notifier.NotifyAgentStatusChanged(new
        {
            agentName = req.Name,
            status,
            timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
        });

        var healthy = status != "Unhealthy";
        return Results.Ok(new
        {
            name = req.Name,
            address = req.Address,
            status,
            healthy,
            message = healthy
                ? $"Agent '{req.Name}' registered and online ({status})."
                : $"Agent '{req.Name}' registered but unhealthy.",
            detail
        });
    }

    /// <summary>DELETE /api/agents/{name} � unregister an agent.</summary>
    private static IResult UnregisterAgent(string name, AgentRegistry registry, AgentGrpcClientManager grpcManager)
    {
        if (!registry.TryGet(name, out var entry))
            return Results.NotFound($"Agent '{name}' not found.");

        grpcManager.RemoveChannel(entry.Address);
        registry.Unregister(name);
        return Results.Ok(new { message = $"Agent '{name}' unregistered." });
    }

    /// <summary>POST /api/agents/{name}/test � test connectivity via GetState.</summary>
    private static async Task<IResult> TestAgent(
        string name,
        AgentRegistry registry,
        AgentGrpcClientManager grpcManager,
        IRealtimeNotifier notifier,
        IAppLogger logger)
    {
        if (!registry.TryGet(name, out var entry))
            return Results.NotFound($"Agent '{name}' not found.");

        try
        {
            var client = grpcManager.GetClient(entry.Address);
            var state = await client.GetStateAsync(new Empty());
            var status = state.State.ToString();

            registry.UpdateStatus(name, status);
            await notifier.NotifyAgentStatusChanged(new
            {
                agentName = name,
                status,
                timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
            });

            return Results.Ok(new { name, status, message = "Connection successful." });
        }
        catch (RpcException ex)
        {
            registry.UpdateStatus(name, "Unreachable", ex.Status.Detail);
            await notifier.NotifyAgentStatusChanged(new
            {
                agentName = name,
                status = "Unreachable",
                timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
            });
            logger.Warn("Agent", $"Agent '{name}' unreachable: {ex.Status.Detail}");
            return Results.Ok(new { name, status = "Unreachable", message = ex.Status.Detail });
        }
        catch (Exception ex)
        {
            logger.Error("Agent", $"Unexpected error testing agent '{name}'", ex);
            registry.UpdateStatus(name, "Error", ex.Message);
            return Results.Problem($"Failed to test agent '{name}': {ex.Message}", statusCode: 502);
        }
    }

    /// <summary>POST /api/agents/{name}/diagnose � run 6-step diagnostic.</summary>
    private static async Task<IResult> DiagnoseAgent(
        string name,
        AgentRegistry registry,
        AgentGrpcClientManager grpcManager,
        IAppLogger logger)
    {
        if (!registry.TryGet(name, out var entry))
            return Results.NotFound($"Agent '{name}' not found.");

        var steps = new List<DiagnosticStep>();

        // Step 1: DNS resolution
        try
        {
            var uri = new Uri(entry.Address);
            var addresses = await System.Net.Dns.GetHostAddressesAsync(uri.Host);
            steps.Add(new("DNS Resolution", true, $"Resolved to {string.Join(", ", addresses.Select(a => a.ToString()))}"));
        }
        catch (Exception ex)
        {
            steps.Add(new("DNS Resolution", false, ex.Message));
            logger.Warn("Agent", $"DNS resolution failed for '{name}': {ex.Message}");
            return Results.Ok(new { name, steps });
        }

        // Step 2: TCP port check
        try
        {
            var uri = new Uri(entry.Address);
            using var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync(uri.Host, uri.Port);
            steps.Add(new("TCP Port Check", true, $"Port {uri.Port} is open."));
        }
        catch (Exception ex)
        {
            steps.Add(new("TCP Port Check", false, ex.Message));
        }

        // Step 3: gRPC GetState
        try
        {
            var client = grpcManager.GetClient(entry.Address);
            var state = await client.GetStateAsync(new Empty());
            steps.Add(new("gRPC GetState", true, $"State: {state.State}"));
        }
        catch (RpcException ex)
        {
            steps.Add(new("gRPC GetState", false, $"{ex.StatusCode}: {ex.Status.Detail}"));
        }

        // Step 4: gRPC GetAgentSnapshot
        try
        {
            var client = grpcManager.GetClient(entry.Address);
            var snapshot = await client.GetAgentSnapshotAsync(new Empty());
            steps.Add(new("Agent Snapshot", true,
                $"Activity: {snapshot.CurrentActivity}, Completed: {snapshot.ExecutionsCompleted}, Failed: {snapshot.ExecutionsFailed}"));
        }
        catch (RpcException ex)
        {
            steps.Add(new("Agent Snapshot", false, $"{ex.StatusCode}: {ex.Status.Detail}"));
        }

        // Step 5: gRPC GetConnectionHealth
        try
        {
            var client = grpcManager.GetClient(entry.Address);
            var health = await client.GetConnectionHealthAsync(new ConnectionHealthRequest());
            steps.Add(new("Connection Health", true,
                $"Connected: {health.IsConnected}, Heartbeats: {health.TotalHeartbeatsSent}, Failures: {health.ConsecutiveFailures}"));
        }
        catch (RpcException ex)
        {
            steps.Add(new("Connection Health", false, $"{ex.StatusCode}: {ex.Status.Detail}"));
        }

        // Step 6: gRPC GetExecutionHistory (latest 1)
        try
        {
            var client = grpcManager.GetClient(entry.Address);
            var history = await client.GetExecutionHistoryAsync(new ExecutionHistoryRequest { MaxResults = 1 });
            var count = history.Records.Count;
            steps.Add(new("Execution History", true, count > 0
                ? $"Last execution: {history.Records[0].Command} (exit {history.Records[0].ExitCode})"
                : "No execution history."));
        }
        catch (RpcException ex)
        {
            steps.Add(new("Execution History", false, $"{ex.StatusCode}: {ex.Status.Detail}"));
        }

        var passed = steps.Count(s => s.Passed);
        registry.UpdateStatus(name, passed == steps.Count ? "Healthy" : $"{passed}/{steps.Count} checks passed");

        return Results.Ok(new { name, steps, summary = $"{passed}/{steps.Count} diagnostic steps passed" });
    }

    /// <summary>GET /api/agents/{name}/snapshot � get agent snapshot via gRPC.</summary>
    private static async Task<IResult> GetAgentSnapshot(
        string name, AgentRegistry registry, AgentGrpcClientManager grpcManager)
    {
        if (!registry.TryGet(name, out var entry))
            return Results.NotFound($"Agent '{name}' not found.");

        try
        {
            var client = grpcManager.GetClient(entry.Address);
            var snapshot = await client.GetAgentSnapshotAsync(new Empty());
            return Results.Ok(new
            {
                snapshot.AgentName,
                State = snapshot.State.ToString(),
                snapshot.CurrentActivity,
                snapshot.CurrentExecutionId,
                snapshot.CurrentCommand,
                snapshot.ExecutionsCompleted,
                snapshot.ExecutionsFailed,
                Metrics = snapshot.Metrics is not null ? new
                {
                    snapshot.Metrics.CpuUsagePct,
                    snapshot.Metrics.MemoryUsedMb,
                    snapshot.Metrics.MemoryTotalMb,
                    snapshot.Metrics.DiskFreeGb,
                    snapshot.Metrics.ActiveProcessCount
                } : null
            });
        }
        catch (RpcException ex)
        {
            return Results.Problem($"gRPC error: {ex.Status.Detail}", statusCode: 502);
        }
    }

    /// <summary>GET /api/agents/{name}/health � get connection health via gRPC.</summary>
    private static async Task<IResult> GetAgentHealth(
        string name, AgentRegistry registry, AgentGrpcClientManager grpcManager)
    {
        if (!registry.TryGet(name, out var entry))
            return Results.NotFound($"Agent '{name}' not found.");

        try
        {
            var client = grpcManager.GetClient(entry.Address);
            var health = await client.GetConnectionHealthAsync(new ConnectionHealthRequest());
            return Results.Ok(new
            {
                health.ControllerName,
                health.ControllerAddress,
                health.IsConnected,
                health.ConsecutiveFailures,
                health.TotalHeartbeatsSent,
                health.TotalHeartbeatsFailed,
                health.CurrentSuccessStreak,
                health.AvgHeartbeatLatencyMs,
                health.EventStreamActive,
                health.LastDisconnectReason
            });
        }
        catch (RpcException ex)
        {
            return Results.Problem($"gRPC error: {ex.Status.Detail}", statusCode: 502);
        }
    }

    /// <summary>GET /api/agents/{name}/history?max=20&filter= � get execution history via gRPC.</summary>
    private static async Task<IResult> GetAgentHistory(
        string name, HttpContext context, AgentRegistry registry, AgentGrpcClientManager grpcManager)
    {
        if (!registry.TryGet(name, out var entry))
            return Results.NotFound($"Agent '{name}' not found.");

        var max = int.TryParse(context.Request.Query["max"], out var m) ? m : 20;
        var filter = context.Request.Query["filter"].ToString();

        try
        {
            var client = grpcManager.GetClient(entry.Address);
            var history = await client.GetExecutionHistoryAsync(new ExecutionHistoryRequest
            {
                MaxResults = max,
                FilterCommand = filter ?? ""
            });

            var records = history.Records.Select(r => new
            {
                r.ExecutionId,
                r.Command,
                r.Arguments,
                Started = r.Started?.ToDateTimeOffset(),
                Finished = r.Finished?.ToDateTimeOffset(),
                r.ExitCode,
                r.ErrorMessage,
                Outcome = r.Outcome.ToString()
            });

            return Results.Ok(records);
        }
        catch (RpcException ex)
        {
            return Results.Problem($"gRPC error: {ex.Status.Detail}", statusCode: 502);
        }
    }

    /// <summary>GET /api/agents/{name}/audit?from=&to=&filter=&max= � get audit log via gRPC.</summary>
    private static async Task<IResult> GetAgentAudit(
        string name, HttpContext context, AgentRegistry registry, AgentGrpcClientManager grpcManager)
    {
        if (!registry.TryGet(name, out var entry))
            return Results.NotFound($"Agent '{name}' not found.");

        var from = context.Request.Query["from"].ToString();
        var to = context.Request.Query["to"].ToString();
        var filter = context.Request.Query["filter"].ToString();
        var max = int.TryParse(context.Request.Query["max"], out var m) ? m : 500;

        try
        {
            var client = grpcManager.GetClient(entry.Address);
            var audit = await client.GetAuditLogAsync(new AuditLogRequest
            {
                FromDate = from ?? "",
                ToDate = to ?? "",
                EventFilter = filter ?? "",
                MaxEntries = max
            });

            var entries = audit.Entries.Select(e => new
            {
                e.Timestamp,
                e.Event,
                e.Severity,
                e.ExecutionId,
                e.Source,
                e.Command,
                e.Detail,
                e.ExitCode,
                e.DurationMs
            });

            return Results.Ok(entries);
        }
        catch (RpcException ex)
        {
            return Results.Problem($"gRPC error: {ex.Status.Detail}", statusCode: 502);
        }
    }

    /// <summary>GET /api/agents/fleet — returns all agents with lock status and session info.</summary>
    private static IResult GetFleet(
        AgentRegistry registry,
        AgentLockManager lockManager,
        ExecutionSessionManager sessionManager)
    {
        var agents = registry.GetAll();
        var locks = lockManager.GetAllLocks();
        var lockLookup = locks.ToDictionary(l => l.AgentName, StringComparer.OrdinalIgnoreCase);

        var fleet = agents.Select(a =>
        {
            lockLookup.TryGetValue(a.Name, out var agentLock);
            return new
            {
                a.Name,
                a.Address,
                a.Status,
                a.LastStatusDetail,
                LastCheckedUtc = a.LastCheckedUtc,
                IsLocked = agentLock != null,
                LockedBy = agentLock?.SessionId,
                LockSource = agentLock?.Source,
                LockedAtUtc = agentLock?.LockedAtUtc,
                WatchItemTag = agentLock?.WatchItemTag
            };
        });

        return Results.Ok(new { agents = fleet, lockVersion = lockManager.Version });
    }

    /// <summary>GET /api/agents/{name}/details — combined snapshot + lock + session info.</summary>
    private static async Task<IResult> GetDetails(
        string name,
        AgentRegistry registry,
        AgentGrpcClientManager grpcManager,
        AgentLockManager lockManager)
    {
        if (!registry.TryGet(name, out var entry))
            return Results.NotFound($"Agent '{name}' not found.");

        object? snapshot = null;
        string? error = null;

        try
        {
            var client = grpcManager.GetClient(entry.Address);
            var s = await client.GetAgentSnapshotAsync(new Empty());
            snapshot = new
            {
                s.AgentName,
                State = s.State.ToString(),
                s.CurrentActivity,
                s.CurrentExecutionId,
                s.CurrentCommand,
                ExecutionStarted = s.ExecutionStarted?.ToDateTimeOffset(),
                AgentStarted = s.AgentStarted?.ToDateTimeOffset(),
                s.ExecutionsCompleted,
                s.ExecutionsFailed,
                Metrics = s.Metrics != null ? new
                {
                    s.Metrics.CpuUsagePct,
                    s.Metrics.MemoryUsedMb,
                    s.Metrics.MemoryTotalMb,
                    s.Metrics.DiskFreeGb,
                    s.Metrics.ActiveProcessCount
                } : null
            };
        }
        catch (RpcException ex)
        {
            error = $"gRPC error: {ex.Status.Detail}";
        }

        var locks = lockManager.GetAllLocks();
        var agentLock = locks.FirstOrDefault(l =>
            string.Equals(l.AgentName, name, StringComparison.OrdinalIgnoreCase));

        return Results.Ok(new
        {
            entry.Name,
            entry.Address,
            entry.Status,
            entry.LastStatusDetail,
            entry.LastCheckedUtc,
            Snapshot = snapshot,
            SnapshotError = error,
            Lock = agentLock != null ? new
            {
                agentLock.SessionId,
                agentLock.Source,
                agentLock.WatchItemTag,
                agentLock.LockedAtUtc
            } : null
        });
    }

    /// <summary>GET /api/agents/{name}/telemetry — lightweight metrics-only snapshot for polling.</summary>
    private static async Task<IResult> GetTelemetry(
        string name, AgentRegistry registry, AgentGrpcClientManager grpcManager)
    {
        if (!registry.TryGet(name, out var entry))
            return Results.NotFound($"Agent '{name}' not found.");

        try
        {
            var client = grpcManager.GetClient(entry.Address);
            var s = await client.GetAgentSnapshotAsync(new Empty());
            return Results.Ok(new
            {
                s.AgentName,
                State = s.State.ToString(),
                s.CurrentActivity,
                s.CurrentCommand,
                s.ExecutionsCompleted,
                s.ExecutionsFailed,
                CpuUsagePct = s.Metrics?.CpuUsagePct ?? 0,
                MemoryUsedMb = s.Metrics?.MemoryUsedMb ?? 0,
                MemoryTotalMb = s.Metrics?.MemoryTotalMb ?? 0,
                DiskFreeGb = s.Metrics?.DiskFreeGb ?? 0,
                ActiveProcessCount = s.Metrics?.ActiveProcessCount ?? 0,
                Timestamp = DateTime.UtcNow
            });
        }
        catch (RpcException ex)
        {
            return Results.Problem($"gRPC error: {ex.Status.Detail}", statusCode: 502);
        }
    }

    /// <summary>PUT /api/agents/{name} — update agent address (re-registration).</summary>
    private static async Task<IResult> UpdateAgent(
        string name, AgentRegisterRequest body, AgentRegistry registry, IRealtimeNotifier notifier)
    {
        if (!registry.TryGet(name, out _))
            return Results.NotFound($"Agent '{name}' not found.");

        // Unregister + re-register with new address
        registry.Unregister(name);
        registry.Register(body.Name ?? name, body.Address);
        await notifier.NotifyAgentStatusChanged(new
        {
            agentName = body.Name ?? name,
            status = "Registered",
            timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
        });
        return Results.Ok(new { updated = true, name = body.Name ?? name, body.Address });
    }
}

public record AgentRegisterRequest(string Name, string Address);
public record DiagnosticStep(string Step, bool Passed, string Detail);
