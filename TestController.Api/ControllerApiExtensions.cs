using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TestController.Api.Hubs;
using TestController.Api.Services;

namespace TestController.Api;

/// <summary>
/// Extension methods for registering the shared API layer into any host
/// (WPF-hosted Kestrel or standalone WebApi).
/// </summary>
public static class ControllerApiExtensions
{
    /// <summary>
    /// Registers the shared API controllers, SignalR hub services, and bridge
    /// into the DI container. Call this from both the WPF host and the standalone WebApi.
    /// Returns the IMvcBuilder so callers can chain .AddJsonOptions() etc.
    /// </summary>
    public static IMvcBuilder AddControllerApi(this IServiceCollection services)
    {
        services.AddSingleton<SignalRBridge>();

        // Add controllers from the shared assembly
        return services.AddControllers()
            .AddApplicationPart(typeof(ControllerApiExtensions).Assembly);
    }

    /// <summary>
    /// Maps shared API controllers and the SignalR hub, then starts the bridge.
    /// Call this after building the WebApplication.
    /// </summary>
    public static WebApplication UseControllerApi(this WebApplication app, string hubPath = "/hubs/controller")
    {
        app.MapControllers();
        app.MapHub<ControllerHub>(hubPath);

        // Start the bridge to wire up service events ? SignalR broadcasts
        var bridge = app.Services.GetRequiredService<SignalRBridge>();
        bridge.Start();

        return app;
    }
}
