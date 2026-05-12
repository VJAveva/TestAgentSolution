using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TestController.Api.Hubs;
using TestController.Api.Middleware;
using TestController.Api.Services;
using TestControllerGrpc.Services;

namespace TestController.Api;

/// <summary>
/// Extension methods for registering the shared API layer into any host
/// (WPF-hosted Kestrel or standalone WebApi).
/// </summary>
public static class ControllerApiExtensions
{
    /// <summary>
    /// Registers the shared API controllers, SignalR hub services, and unified
    /// real-time notifier into the DI container. Call this from both the WPF host
    /// and the standalone WebApi.
    /// Returns the IMvcBuilder so callers can chain .AddJsonOptions() etc.
    /// </summary>
    public static IMvcBuilder AddControllerApi(this IServiceCollection services)
    {
        // AgentLockManager: only register if not already provided by the host (WPF bridges its own instance)
        if (!services.Any(d => d.ServiceType == typeof(AgentLockManager)))
        {
            var lockFilePath = Path.Combine(AppLogger.DefaultLogDirectory, "agent-locks.json");
            services.AddSingleton(new AgentLockManager(lockFilePath));
        }

        services.AddSingleton<CachedBuildResultsProvider>();
        services.AddSingleton<FailurePatternAnalyzer>();
        services.AddSingleton<ExecutionLogCorrelator>();
        services.AddSingleton<SignalRNotifier>();
        services.AddSingleton<IRealtimeNotifier>(sp => sp.GetRequiredService<SignalRNotifier>());

        // Lock recovery: validates persisted locks against agent state on startup
        services.AddHostedService<LockRecoveryService>();

        // Add controllers from the shared assembly
        return services.AddControllers()
            .AddApplicationPart(typeof(ControllerApiExtensions).Assembly);
    }

    /// <summary>
    /// Maps shared API controllers and the single SignalR hub, then starts the notifier.
    /// Call this after building the WebApplication.
    /// </summary>
    public static WebApplication UseControllerApi(this WebApplication app, string hubPath = "/hubs/controller")
    {
        // Request correlation + logging middleware — must be before controllers
        app.UseMiddleware<RequestLoggingMiddleware>();

        app.MapControllers();
        app.MapHub<ControllerHub>(hubPath);

        // Start the unified notifier to wire up service events ? SignalR broadcasts
        var notifier = app.Services.GetRequiredService<SignalRNotifier>();
        notifier.Start();

        return app;
    }
}
