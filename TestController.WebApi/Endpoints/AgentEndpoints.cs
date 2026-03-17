using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.SignalR;
using TestAgentGrpc;
using TestController.WebApi.Hubs;
using TestController.WebApi.Services;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Endpoints;

public static class AgentEndpoints
{
    public static RouteGroupBuilder MapAgentEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", ListAgents);
        group.MapPost("/register", RegisterAgent);
        group.MapDelete("/{name}", UnregisterAgent);
        group.MapPost("/{name}/test", TestAgent);
        group.MapPost("/{name}/diagnose", DiagnoseAgent);
        group.MapGet("/{name}/snapshot", GetAgentSnapshot);
        group.MapGet("/{name}/health", GetAgentHealth);
        group.MapGet("/{name}/history", GetAgentHistory);
        group.MapGet("/{name}/audit", GetAgentAudit);
        return group;
    }

    /// <summary>GET /api/agents — list all registered agents with status.</summary>
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

    /// <summary>POST /api/agents/register — register a new agent.</summary>
    private static IResult RegisterAgent(AgentRegisterRequest req, AgentRegistry registry)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Address))
            return Results.BadRequest("Name and Address are required.");

        registry.Register(req.Name, req.Address);
        return Results.Ok(new { message = $"Agent '{req.Name}' registered at {req.Address}." });
    }

    /// <summary>DELETE /api/agents/{name} — unregister an agent.</summary>
    private static IResult UnregisterAgent(string name, AgentRegistry registry, AgentGrpcClientManager grpcManager)
    {
        if (!registry.TryGet(name, out var entry))
            return Results.NotFound($"Agent '{name}' not found.");

        grpcManager.RemoveChannel(entry.Address);
        registry.Unregister(name);
        return Results.Ok(new { message = $"Agent '{name}' unregistered." });
    }

    /// <summary>POST /api/agents/{name}/test — test connectivity via GetState.</summary>
    private static async Task<IResult> TestAgent(
        string name,
        AgentRegistry registry,
        AgentGrpcClientManager grpcManager,
        IHubContext<LiveHub> hub,
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
            await hub.Clients.All.SendAsync("AgentStatus", name, status);

            return Results.Ok(new { name, status, message = "Connection successful." });
        }
        catch (RpcException ex)
        {
            registry.UpdateStatus(name, "Unreachable", ex.Status.Detail);
            await hub.Clients.All.SendAsync("AgentStatus", name, "Unreachable");
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

    /// <summary>POST /api/agents/{name}/diagnose — run 6-step diagnostic.</summary>
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

    /// <summary>GET /api/agents/{name}/snapshot — get agent snapshot via gRPC.</summary>
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

    /// <summary>GET /api/agents/{name}/health — get connection health via gRPC.</summary>
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

    /// <summary>GET /api/agents/{name}/history?max=20&filter= — get execution history via gRPC.</summary>
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

    /// <summary>GET /api/agents/{name}/audit?from=&to=&filter=&max= — get audit log via gRPC.</summary>
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
}

public record AgentRegisterRequest(string Name, string Address);
public record DiagnosticStep(string Step, bool Passed, string Detail);
