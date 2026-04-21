using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Hubs;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Hosts an ASP.NET Core server (REST API + SignalR hub) alongside
/// the WPF application. Shares the same singleton services so that
/// triggering from the browser runs the same code path as the desktop.
///
/// Default port: 5200 (configurable via WebApiPort in appsettings).
/// </summary>
public sealed class ControllerWebApiHost : IHostedService, IDisposable
{
    private readonly ExecutionSessionManager _sessionManager;
    private readonly IActionPipelineExecutor _executor;
    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly IFileWatcherManager _watcherManager;
    private readonly IVocabularyMonitor _vocabMonitor;
    private readonly IEventAggregator _events;
    private readonly TrxResultsParser _parser;
    private readonly BuildResultsAggregator _aggregator;
    private readonly BuildResultsConfig _resultsConfig;
    private readonly ILogger<ControllerWebApiHost> _logger;
    private readonly int _port;
    private WebApplication? _app;
    private Task? _serverTask;

    public ControllerWebApiHost(
        ExecutionSessionManager sessionManager,
        IActionPipelineExecutor executor,
        IAgentGrpcDispatcher dispatcher,
        IFileWatcherManager watcherManager,
        IVocabularyMonitor vocabMonitor,
        IEventAggregator events,
        TrxResultsParser parser,
        BuildResultsAggregator aggregator,
        BuildResultsConfig resultsConfig,
        IConfiguration config,
        ILogger<ControllerWebApiHost> logger)
    {
        _sessionManager = sessionManager;
        _executor = executor;
        _dispatcher = dispatcher;
        _watcherManager = watcherManager;
        _vocabMonitor = vocabMonitor;
        _events = events;
        _parser = parser;
        _aggregator = aggregator;
        _resultsConfig = resultsConfig;
        _logger = logger;
        _port = config.GetValue<int>("WebApiPort", 5200);
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
                kestrel.ListenAnyIP(_port, o =>
                {
                    o.Protocols = HttpProtocols.Http1AndHttp2;
                });
            });

            // Share singletons from WPF DI into the WebApi server's DI container
            builder.Services.AddSingleton(_sessionManager);
            builder.Services.AddSingleton(_executor);
            builder.Services.AddSingleton(_dispatcher);
            builder.Services.AddSingleton(_watcherManager);
            builder.Services.AddSingleton(_vocabMonitor);
            builder.Services.AddSingleton(_events);
            builder.Services.AddSingleton(_parser);
            builder.Services.AddSingleton(_aggregator);
            builder.Services.AddSingleton(_resultsConfig);

            builder.Services.AddControllers()
                .AddJsonOptions(o =>
                {
                    o.JsonSerializerOptions.PropertyNamingPolicy =
                        System.Text.Json.JsonNamingPolicy.CamelCase;
                    o.JsonSerializerOptions.WriteIndented = false;
                });

            builder.Services.AddSignalR(o =>
            {
                o.EnableDetailedErrors = true;
                o.MaximumReceiveMessageSize = 128 * 1024;
            });

            builder.Services.AddCors(options =>
            {
                options.AddPolicy("WebClient", policy =>
                {
                    policy.WithOrigins(
                            "http://localhost:3000",
                            "http://localhost:5173",
                            "http://localhost:8081",
                            $"http://{Environment.MachineName}:8081",
                            $"http://{Environment.MachineName}")
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        .AllowCredentials();
                });
            });

            builder.Services.AddSingleton<SignalRBridge>();

            builder.Logging.SetMinimumLevel(LogLevel.Information);
            builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

            _app = builder.Build();

            // Request logging middleware — logs every API call with timing
            _app.Use(async (context, next) =>
            {
                var path = context.Request.Path;
                var method = context.Request.Method;
                var sw = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    await next();
                    sw.Stop();
                    _logger.LogInformation(
                        "API {Method} {Path} -> {Status} ({Elapsed}ms)",
                        method, path, context.Response.StatusCode, sw.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex,
                        "API {Method} {Path} -> EXCEPTION ({Elapsed}ms): {Error}",
                        method, path, sw.ElapsedMilliseconds, ex.Message);
                    throw;
                }
            });

            _app.UseCors("WebClient");
            _app.MapControllers();
            _app.MapHub<ControllerHub>("/hubs/controller");

            // Start the bridge after the hub is mapped
            var bridge = _app.Services.GetRequiredService<SignalRBridge>();
            bridge.Start();

            _logger.LogInformation("WebApi + SignalR server listening on port {Port}", _port);
            await _app.RunAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebApi server failed on port {Port}", _port);
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _logger.LogInformation("WebApi + SignalR server stopping…");

        // Dispose the bridge first to unsubscribe from all events
        if (_app is not null)
        {
            var bridge = _app.Services.GetService<SignalRBridge>();
            bridge?.Dispose();
        }

        if (_app is not null)
            await _app.StopAsync(ct);
        if (_serverTask is not null)
            try { await _serverTask.WaitAsync(ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "WebApi server stop interrupted"); }
    }

    public void Dispose()
    {
        if (_app is not null)
        {
            try { _app.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch { /* best effort during shutdown */ }
        }
    }
}
