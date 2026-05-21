using TestController.WebApi.Services;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Endpoints;

public static class DeploymentEndpoints
{
    private static volatile bool _maintenanceMode;
    private static string? _maintenanceReason;
    private static DateTime? _maintenanceSince;

    public static RouteGroupBuilder MapDeploymentEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/status", GetDeploymentStatus);
        group.MapPost("/maintenance/enable", EnableMaintenance);
        group.MapPost("/maintenance/disable", DisableMaintenance);
        group.MapGet("/preflight", PreDeploymentCheck);
        return group;
    }

    public static bool IsMaintenanceMode => _maintenanceMode;

    /// <summary>GET /api/deployment/status — current deployment/maintenance status.</summary>
    private static IResult GetDeploymentStatus(ExecutionSessionManager sessionManager)
    {
        var activeSessions = sessionManager.GetActiveSessions();
        return Results.Ok(new
        {
            maintenanceMode = _maintenanceMode,
            reason = _maintenanceReason,
            since = _maintenanceSince,
            activeSessionCount = activeSessions.Count,
            activeSessions = activeSessions.Select(s => new
            {
                s.SessionId,
                s.WatchItemTag,
                s.StartedUtc
            })
        });
    }

    /// <summary>POST /api/deployment/maintenance/enable — put the system in maintenance mode.</summary>
    private static IResult EnableMaintenance(HttpContext context, ExecutionSessionManager sessionManager)
    {
        var reason = context.Request.Query["reason"].ToString();
        var force = string.Equals(context.Request.Query["force"], "true", StringComparison.OrdinalIgnoreCase);

        var activeSessions = sessionManager.GetActiveSessions();
        if (activeSessions.Count > 0 && !force)
        {
            return Results.Conflict(new
            {
                error = "Cannot enable maintenance mode while sessions are active.",
                activeSessionCount = activeSessions.Count,
                activeSessions = activeSessions.Select(s => new
                {
                    s.SessionId,
                    s.WatchItemTag,
                    s.StartedUtc
                }),
                hint = "Use ?force=true to override."
            });
        }

        _maintenanceMode = true;
        _maintenanceReason = string.IsNullOrWhiteSpace(reason) ? "Deployment in progress" : reason;
        _maintenanceSince = DateTime.UtcNow;

        return Results.Ok(new
        {
            maintenanceMode = true,
            reason = _maintenanceReason,
            since = _maintenanceSince,
            message = "Maintenance mode enabled. New executions will be blocked."
        });
    }

    /// <summary>POST /api/deployment/maintenance/disable — take the system out of maintenance mode.</summary>
    private static IResult DisableMaintenance()
    {
        _maintenanceMode = false;
        var previousReason = _maintenanceReason;
        _maintenanceReason = null;
        _maintenanceSince = null;

        return Results.Ok(new
        {
            maintenanceMode = false,
            message = "Maintenance mode disabled. System is operational.",
            previousReason
        });
    }

    /// <summary>GET /api/deployment/preflight — pre-deployment readiness check.</summary>
    private static IResult PreDeploymentCheck(
        ExecutionSessionManager sessionManager,
        AgentRegistry registry,
        AgentLockManager lockManager)
    {
        var activeSessions = sessionManager.GetActiveSessions();
        var allLocks = lockManager.GetAllLocks();
        var agents = registry.GetAll();

        var issues = new List<string>();
        var warnings = new List<string>();

        if (activeSessions.Count > 0)
            issues.Add($"{activeSessions.Count} active session(s) running — deployment may interrupt executions.");

        if (allLocks.Count > 0)
            warnings.Add($"{allLocks.Count} agent lock(s) held — locked agents may be mid-execution.");

        var offlineAgents = agents.Where(a =>
            string.Equals(a.Status, "Offline", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a.Status, "Unreachable", StringComparison.OrdinalIgnoreCase)).ToList();
        if (offlineAgents.Count > 0)
            warnings.Add($"{offlineAgents.Count} agent(s) offline: {string.Join(", ", offlineAgents.Select(a => a.Name))}");

        var safe = issues.Count == 0;

        return Results.Ok(new
        {
            safe,
            issues,
            warnings,
            summary = safe
                ? "System is safe to deploy."
                : "Deployment NOT recommended — resolve issues or use -Force.",
            agents = new
            {
                total = agents.Count,
                offline = offlineAgents.Count,
                locked = allLocks.Count
            },
            activeSessions = activeSessions.Select(s => new
            {
                s.SessionId,
                s.WatchItemTag,
                s.StartedUtc
            })
        });
    }
}
