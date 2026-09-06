using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TestController.Api;
using TestController.Api.Security;
using TestController.Api.Services;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Maintenance;
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
    private readonly AgentLockManager _lockManager;
    private readonly IMaintenanceStateStore? _maintenanceState;
    private readonly IFleetMaintenanceService? _fleetMaintenance;
    private readonly IMaintenanceOperationStore? _maintenanceStore;
    private readonly INodeUpdateStatusStore? _updateStatus;
    private readonly IFleetNotificationService? _fleetNotifications;
    private readonly IUpdatePolicyStore? _updatePolicy;
    private readonly IAppLogger _appLogger;
    private readonly IRegressionImpactMatcher _impactMatcher;
    private readonly ILogger<ControllerWebApiHost> _logger;
    private readonly IConfiguration _config;
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
        AgentLockManager lockManager,
        IAppLogger appLogger,
        IRegressionImpactMatcher impactMatcher,
        IConfiguration config,
        ILogger<ControllerWebApiHost> logger,
        IMaintenanceStateStore? maintenanceState = null,
        IFleetMaintenanceService? fleetMaintenance = null,
        IMaintenanceOperationStore? maintenanceStore = null,
        INodeUpdateStatusStore? updateStatus = null,
        IFleetNotificationService? fleetNotifications = null,
        IUpdatePolicyStore? updatePolicy = null)
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
        _lockManager = lockManager;
        _maintenanceState = maintenanceState;
        _fleetMaintenance = fleetMaintenance;
        _maintenanceStore = maintenanceStore;
        _updateStatus = updateStatus;
        _fleetNotifications = fleetNotifications;
        _updatePolicy = updatePolicy;
        _appLogger = appLogger;
        _impactMatcher = impactMatcher;
        _logger = logger;
        _config = config;
        _port = config.GetValue<int>("WebApiPort", 5200);
    }

    public Task StartAsync(CancellationToken ct)
    {
        // Fail fast if required WPF-bridge services are not wired
        ValidateBridgedDependencies();
        _serverTask = Task.Run(() => RunServerAsync(ct), ct);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Validates that all required WPF-bridged services are non-null before starting the server.
    /// Fail-fast prevents silent runtime failures from missing DI registrations.
    /// </summary>
    private void ValidateBridgedDependencies()
    {
        var problems = new List<string>();

        if (_dispatcher is null) problems.Add(nameof(IAgentGrpcDispatcher));
        if (_sessionManager is null) problems.Add(nameof(ExecutionSessionManager));
        if (_executor is null) problems.Add(nameof(IActionPipelineExecutor));
        if (_lockManager is null) problems.Add(nameof(AgentLockManager));
        if (_events is null) problems.Add(nameof(IEventAggregator));
        if (_appLogger is null) problems.Add(nameof(IAppLogger));

        if (problems.Count > 0)
        {
            var msg = $"Embedded WebApi host cannot start — missing bridged services: {string.Join(", ", problems)}";
            _logger.LogCritical(msg);
            throw new InvalidOperationException(msg);
        }

        _logger.LogDebug("Embedded WebApi host dependency validation passed");
    }

    private async Task RunServerAsync(CancellationToken ct)
    {
        try
        {
            var builder = WebApplication.CreateBuilder();

            // Import the WPF app's configuration (so Security section is available)
            builder.Configuration.AddConfiguration(_config);

            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                kestrel.ListenAnyIP(_port, o =>
                {
                    o.Protocols = HttpProtocols.Http1AndHttp2;
                });
                // Allow long-running executions triggered via WebClient.
                kestrel.Limits.KeepAliveTimeout = TimeSpan.FromHours(4);
                kestrel.Limits.MinRequestBodyDataRate = null;
                kestrel.Limits.MinResponseDataRate = null;
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
            builder.Services.AddSingleton(_lockManager);
            if (_maintenanceState is not null)
                builder.Services.AddSingleton(_maintenanceState);
            if (_fleetMaintenance is not null)
                builder.Services.AddSingleton(_fleetMaintenance);
            if (_maintenanceStore is not null)
                builder.Services.AddSingleton(_maintenanceStore);
            // Same instances the WPF UI binds to, so both surfaces agree on update posture.
            if (_updateStatus is not null)
                builder.Services.AddSingleton(_updateStatus);
            if (_fleetNotifications is not null)
                builder.Services.AddSingleton(_fleetNotifications);
            if (_updatePolicy is not null)
                builder.Services.AddSingleton(_updatePolicy);
            builder.Services.AddSingleton(_appLogger);

            // Bridge the impact-mapping matcher (with its engine + index) from the WPF container so the
            // React client on this embedded host gets the same "Impacted Test Cases" data as the desktop.
            builder.Services.AddSingleton(_impactMatcher);

            // RBAC feature (identity, authorization, audit, persistence, mode transition)
            builder.Services.AddRbacFeature(builder.Configuration);

            // Register security services (ISessionOwnershipChecker, ISecurityAuditLogger, auth)
            builder.Services.AddMultiIdentitySecurity(builder.Configuration);

            // Use the shared API library for controllers, hub, and bridge
            builder.Services.AddControllerApi()
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
            builder.Services.AddScoped<Microsoft.AspNetCore.SignalR.IHubFilter, TestController.Api.Hubs.HubExceptionFilter>();

            builder.Services.AddCors(options =>
            {
                options.AddPolicy("WebClient", policy =>
                {
                    policy.WithOrigins(
                            "http://localhost:3000",
                            "http://localhost:5173",
                            "http://localhost:8080",
                            "http://localhost:8081",
                            "http://127.0.0.1:3000",
                            "http://127.0.0.1:5173",
                            "http://127.0.0.1:8080",
                            "http://127.0.0.1:8081",
                            $"http://{Environment.MachineName}:3000",
                            $"http://{Environment.MachineName}:8080",
                            $"http://{Environment.MachineName}:8081",
                            $"http://{Environment.MachineName}")
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        .AllowCredentials();
                });
            });

            builder.Logging.SetMinimumLevel(LogLevel.Information);
            builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

            // Prometheus /metrics endpoint so the WPF host ships telemetry too (P2-2).
            builder.Services.AddControllerHostMetrics();

            _app = builder.Build();

            _app.UseCors("WebClient");

            // Authentication + authorization middleware
            _app.UseMultiIdentitySecurity();

            // Map shared controllers, hub, and start the SignalR bridge
            // (UseControllerApi registers RequestLoggingMiddleware)
            _app.UseControllerApi();

            // Prometheus scraping endpoint on this host's listener.
            _app.MapControllerHostMetrics(_app.Services);

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
        _logger.LogInformation("WebApi + SignalR server stopping�");

        // Dispose the notifier first to unsubscribe from all events
        if (_app is not null)
        {
            var notifier = _app.Services.GetService<SignalRNotifier>();
            notifier?.Dispose();
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
