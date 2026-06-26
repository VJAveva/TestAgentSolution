using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using TestControllerGrpc.Services;

namespace TestController.Api.Services;

/// <summary>
/// Custom controller-host metrics exposed via OpenTelemetry / Prometheus.
/// Mirrors the WebApi <c>AppMetrics</c> pattern so the WPF-embedded host ships
/// the same shape of telemetry on its own <c>/metrics</c> endpoint. Uses
/// observable instruments that pull live state — no execution-path wiring needed.
/// </summary>
public sealed class ControllerHostMetrics
{
    public const string MeterName = "TestController.Controller";

    public ControllerHostMetrics(
        IMeterFactory meterFactory,
        ExecutionSessionManager sessions,
        IAgentGrpcDispatcher dispatcher)
    {
        var meter = meterFactory.Create(MeterName);

        meter.CreateObservableGauge(
            "testcontroller.sessions.active",
            () => sessions.GetActiveSessions().Count,
            description: "Number of currently active execution sessions");

        meter.CreateObservableGauge(
            "testcontroller.agents.registered",
            () => dispatcher.RegisteredAgents.Count(),
            description: "Number of registered agents");

        meter.CreateObservableGauge(
            "testcontroller.agents.healthy",
            () => dispatcher.GetAllAgentHealth().Values.Count(h => h.IsHealthy),
            description: "Number of agents currently reporting healthy");

        meter.CreateObservableGauge(
            "testcontroller.agents.unhealthy",
            () => dispatcher.GetAllAgentHealth().Values.Count(h => !h.IsHealthy),
            description: "Number of agents currently reporting unhealthy");
    }
}

/// <summary>
/// Registration + endpoint helpers for <see cref="ControllerHostMetrics"/>.
/// Encapsulates the OpenTelemetry/Prometheus wiring so hosts can opt in with a
/// single call without referencing the OpenTelemetry APIs directly.
/// </summary>
public static class ControllerHostMetricsExtensions
{
    /// <summary>
    /// Registers controller-host metrics and the OpenTelemetry Prometheus exporter.
    /// </summary>
    public static IServiceCollection AddControllerHostMetrics(this IServiceCollection services)
    {
        services.AddSingleton<ControllerHostMetrics>();
        services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(ControllerHostMetrics.MeterName);
                metrics.AddPrometheusExporter();
            });
        return services;
    }

    /// <summary>
    /// Maps the Prometheus <c>/metrics</c> scraping endpoint and forces the
    /// <see cref="ControllerHostMetrics"/> singleton to instantiate so its
    /// observable instruments are registered before the first scrape.
    /// </summary>
    public static IEndpointRouteBuilder MapControllerHostMetrics(
        this IEndpointRouteBuilder endpoints, IServiceProvider services)
    {
        _ = services.GetRequiredService<ControllerHostMetrics>();
        endpoints.MapPrometheusScrapingEndpoint("/metrics");
        return endpoints;
    }
}
