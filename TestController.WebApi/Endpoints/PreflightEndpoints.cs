using TestController.Api.Controllers;
using TestController.WebApi.Services;
using TestControllerGrpc.Core.Preflight;
using TestControllerGrpc.Locking;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Endpoints;

/// <summary>
/// The full six-group pre-flight report for the web client.
/// </summary>
/// <remarks>
/// Pre-flight must run on the host that will EXECUTE the run, because its path checks depend on the
/// caller's identity. Co-located, that host is the WPF controller, so this proxies to it; the IIS
/// pool runs as a machine account that cannot read the build-drop shares and reported them missing.
/// Standalone, this host is the executor, so the checks run here and are correct.
/// </remarks>
public static class PreflightEndpoints
{
    public static RouteGroupBuilder MapPreflightEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{tag}", Check);
        return group;
    }

    private static async Task<IResult> Check(
        string tag,
        ControllerProxyService proxy,
        HttpContext httpContext,
        WatchListFileService fileService,
        AgentRegistry registry,
        AgentTelemetryCache telemetry,
        AgentLockManager agentLocks,
        ILockRegistry lockRegistry,
        IAppLogger logger)
    {
        if (proxy.IsConfigured)
        {
            var response = await proxy.ForwardPostAsync(
                $"/api/preflight/{Uri.EscapeDataString(tag)}", GetAuthHeader(httpContext));

            if (response is null)
            {
                // Falling back to a local check would reintroduce the wrong-identity answer, so say
                // plainly that the check could not be made rather than produce a misleading one.
                logger.Warn("Preflight", $"Controller unreachable; cannot pre-flight '{tag}' from the web tier.");
                return Results.Problem(
                    "Pre-flight runs on the controller, which is not reachable. Start the controller and retry.",
                    statusCode: 502);
            }

            using (response)
            {
                var content = await response.Content.ReadAsStringAsync();
                return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
            }
        }

        return CheckLocally(tag, fileService, registry, telemetry, agentLocks, lockRegistry, logger);
    }

    /// <summary>Standalone mode: this host dispatches the run, so its own view is the right one.</summary>
    private static IResult CheckLocally(
        string tag,
        WatchListFileService fileService,
        AgentRegistry registry,
        AgentTelemetryCache telemetry,
        AgentLockManager agentLocks,
        ILockRegistry lockRegistry,
        IAppLogger logger)
    {
        WatchListConfig config;
        try
        {
            config = fileService.Load();
        }
        catch (Exception ex)
        {
            return Results.Problem($"Failed to load WatchList: {ex.Message}", statusCode: 500);
        }

        var pipeline = config.WatchItems
            .FirstOrDefault(w => string.Equals(w.Tag, tag, StringComparison.OrdinalIgnoreCase));
        if (pipeline is null) return Results.NotFound($"Pipeline '{tag}' not found.");

        var fs = new PreflightFileSystem();
        var runner = new PreflightRunner(fs, name => FactsFor(name, registry, telemetry, agentLocks));

        PreflightReport report;
        try
        {
            report = runner.Run(new PreflightRequest
            {
                Config = config,
                Pipeline = pipeline,
                Scope = PreflightScope.Pipeline,
                PipelineLockedBy = DescribeLockHolder(lockRegistry, tag),
            });
        }
        catch (Exception ex)
        {
            logger.Error("Preflight", $"Pre-flight failed to run for '{tag}'", ex);
            return Results.Problem($"Pre-flight failed to run: {ex.Message}", statusCode: 500);
        }

        logger.Info("Preflight", $"{report.Target}: {report.Summary}");
        return Results.Ok(PreflightResponse.From(report, fs.Identity));
    }

    private static string? GetAuthHeader(HttpContext context) =>
        context.Request.Headers.Authorization.FirstOrDefault();

    private static string? DescribeLockHolder(ILockRegistry lockRegistry, string tag)
    {
        var held = lockRegistry.Get(tag);
        if (held is null || held.Status != LockStatus.Active) return null;
        return $"{held.Owner.DisplayName} ({held.Owner.ClientKind})";
    }

    private static PreflightAgentFacts FactsFor(
        string agentName, AgentRegistry registry, AgentTelemetryCache telemetry, AgentLockManager agentLocks)
    {
        if (!registry.TryGet(agentName, out var entry)) return PreflightAgentFacts.Unknown(agentName);

        var cached = telemetry.Get(agentName)?.Response;
        var agentLock = agentLocks.GetLock(agentName);

        return new PreflightAgentFacts
        {
            AgentName = agentName,
            IsRegistered = true,
            IsOnline = AgentStatusVocabulary.IsOnline(entry.Status),
            IsBusy = cached?.State is "Busy" or "Executing",
            MaintenanceState = "None",
            LockedBy = agentLock is null ? null : $"{agentLock.WatchItemTag} ({agentLock.Source})",
            DiskFreeGb = cached?.DiskFreeGb,
        };
    }
}
