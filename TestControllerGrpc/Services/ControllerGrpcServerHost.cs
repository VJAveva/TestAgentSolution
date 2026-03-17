using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TestControllerGrpc.Services;

/// <summary>
/// Hosts a Kestrel gRPC server alongside the WPF application so that
/// remote agents can call <c>TestControllerService</c> RPCs:
///   • Register / UnRegister
///   • Heartbeat with resource metrics
///   • PushExecutionEvents (streaming)
///
/// Runs on a background thread. The WPF app's DI container provides
/// shared singletons (AgentGrpcDispatcher, etc.).
///
/// Default port: 5100 (configurable via ControllerGrpcPort in appsettings).
/// </summary>
public sealed class ControllerGrpcServerHost : IHostedService, IDisposable
{
    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly IEventAggregator _events;
    private readonly ILogger<ControllerGrpcServerHost> _logger;
    private readonly int _port;
    private WebApplication? _app;
    private Task? _serverTask;

    public ControllerGrpcServerHost(
        IAgentGrpcDispatcher dispatcher,
        IEventAggregator events,
        IConfiguration config,
        ILogger<ControllerGrpcServerHost> logger)
    {
        _dispatcher = dispatcher;
        _events = events;
        _logger = logger;
        _port = config.GetValue<int>("ControllerGrpcPort", 5100);
    }

    public Task StartAsync(CancellationToken ct)
    {
        _serverTask = Task.Run(() => RunServerAsync(ct), ct);
        return Task.CompletedTask;
    }

    private async Task RunServerAsync(CancellationToken ct)
    {
        try
        {
            var builder = WebApplication.CreateBuilder();

            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                kestrel.ListenAnyIP(_port, o => o.Protocols = HttpProtocols.Http2);
            });

            builder.Services.AddGrpc();

            // Share singletons from WPF DI into the gRPC server's DI container
            builder.Services.AddSingleton(_dispatcher);
            builder.Services.AddSingleton(_events);

            // Reduce Kestrel/ASP.NET noise
            builder.Logging.SetMinimumLevel(LogLevel.Warning);

            _app = builder.Build();
            _app.MapGrpcService<TestControllerGrpcService>();

            _logger.LogInformation("Controller gRPC server listening on port {Port}", _port);
            await _app.RunAsync(ct);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Controller gRPC server failed on port {Port}", _port);
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _logger.LogInformation("Controller gRPC server stopping…");
        if (_app is not null)
            await _app.StopAsync(ct);
        if (_serverTask is not null)
            try { await _serverTask.WaitAsync(ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "gRPC server stop interrupted"); }
    }

    public void Dispose()
    {
        _app?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
