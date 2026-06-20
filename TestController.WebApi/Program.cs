using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using TestController.Api;
using TestController.Api.Security;
using TestController.WebApi.Endpoints;
using TestController.WebApi.Services;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

// ── Crash capture — must be first so nothing escapes unglogged ─────────
var logDir = AppLogger.DefaultLogDirectory;
CrashDumpHelper.InstallGlobalHandlers("webapi", logDir);

try
{
var builder = WebApplication.CreateBuilder(args);

// Fail-fast DI validation: detect missing registrations at startup, not first request
builder.Host.UseDefaultServiceProvider(o =>
{
    o.ValidateOnBuild = true;
    o.ValidateScopes = true;
});

// Scale fix: Pre-warm ThreadPool for 200 concurrent agent operations.
// Default min = CPU core count (8-16); under load, .NET adds threads at 500ms/thread.
// At 200 agents with parallel health checks + gRPC streams, need immediate capacity.
ThreadPool.SetMinThreads(workerThreads: 200, completionPortThreads: 200);

// Allow long-running executions triggered via WebClient.
// Without these, Kestrel defaults (130s keepalive, minimum data rates)
// kill connections during long test runs that produce no output for minutes.
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Limits.KeepAliveTimeout = TimeSpan.FromHours(4);
    kestrel.Limits.MinRequestBodyDataRate = null;
    kestrel.Limits.MinResponseDataRate = null;
});

// ?? JSON serializer: camelCase + string enums for React client ??????
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// Shared services from Core
builder.Services.AddSingleton<IWatchListXmlParser, WatchListXmlParserService>();
builder.Services.AddSingleton<TrxResultsParser>();
builder.Services.AddSingleton(sp =>
{
    var rc = new BuildResultsConfig();
    builder.Configuration.GetSection("BuildResults").Bind(rc);
    return rc;
});
builder.Services.AddSingleton<BuildResultsAggregator>();
// CachedBuildResultsProvider is registered by AddControllerApi() below; no need to repeat it here.
builder.Services.AddSingleton<BuildTrendAnalyzer>();
builder.Services.AddSingleton<ConsecutiveFailureDetector>();
builder.Services.AddSingleton<BuildReportHtmlGenerator>();
builder.Services.AddSingleton<ExecutionSessionManager>();
builder.Services.AddSingleton<IAppLogger>(sp =>
{
    var logDir = builder.Configuration["Logging:LogDirectory"]
        ?? builder.Configuration["LogDirectory"]
        ?? AppLogger.DefaultLogDirectory;
    return new AppLogger("webapi", logDir);
});

// Web API specific services
builder.Services.AddSingleton<AgentGrpcClientManager>(sp =>
    new AgentGrpcClientManager(sp.GetRequiredService<GrpcTlsChannelFactory>()));
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<AgentTelemetryCache>();
builder.Services.AddSingleton<WatchListFileService>();
builder.Services.AddSingleton<ControllerProxyService>();
builder.Services.AddSingleton<ConfigValidator>();
builder.Services.AddHostedService<AgentEventRelayService>();

// Adapters: expose standalone services as the interfaces the shared API controllers expect
builder.Services.AddSingleton<IVocabularyMonitor>(sp => new StandaloneVocabularyMonitor(sp.GetRequiredService<WatchListFileService>()));
builder.Services.AddSingleton<IAgentGrpcDispatcher>(sp => new StandaloneAgentDispatcher(
    sp.GetRequiredService<AgentGrpcClientManager>(),
    sp.GetRequiredService<AgentRegistry>(),
    sp.GetRequiredService<ILogger<StandaloneAgentDispatcher>>()));
builder.Services.AddSingleton<IActionPipelineExecutor>(sp => new StandalonePipelineExecutor(
    sp.GetRequiredService<IAgentGrpcDispatcher>(),
    sp.GetRequiredService<ExecutionSessionManager>(),
    sp.GetRequiredService<ILogger<StandalonePipelineExecutor>>()));
builder.Services.AddSingleton<IEventAggregator, EventAggregator>();

// RBAC feature (identity, authorization, audit — NO local DB; routes through controller)
builder.Services.AddRbacFeature(builder.Configuration, isPrimaryHost: false);

// Keep this secondary host's RBAC mode in sync with the controller (source of truth)
// so the IIS-hosted web client switches Default/Secured when the WPF app does.
builder.Services.AddHostedService<SystemModeSyncService>();

// Multi-identity security framework: authentication + authorization + audit
builder.Services.AddMultiIdentitySecurity(builder.Configuration);

// Shared API library: controllers for execution, watchlist, agents, health, results + SignalR hub + bridge
builder.Services.AddControllerApi()
    .ConfigureApplicationPartManager(apm =>
        apm.FeatureProviders.Add(new ProxiedControllerExclusionProvider()))
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

var signalRSection = builder.Configuration.GetSection("SignalR");
builder.Services.AddSignalR(options =>
{
    if (long.TryParse(signalRSection["MaximumReceiveMessageSize"], out var maxMsg))
        options.MaximumReceiveMessageSize = maxMsg;
    if (TimeSpan.TryParse(signalRSection["KeepAliveInterval"], out var keepAlive))
        options.KeepAliveInterval = keepAlive;
    if (TimeSpan.TryParse(signalRSection["ClientTimeoutInterval"], out var clientTimeout))
        options.ClientTimeoutInterval = clientTimeout;
});
builder.Services.AddScoped<Microsoft.AspNetCore.SignalR.IHubFilter, TestController.Api.Hubs.HubExceptionFilter>();

// CORS: production environments should specify allowed origins explicitly.
// Default policy allows all for development/single-machine scenarios.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
builder.Services.AddCors(o =>
{
    if (allowedOrigins is { Length: > 0 })
    {
        o.AddDefaultPolicy(p =>
            p.WithOrigins(allowedOrigins)
             .AllowAnyMethod()
             .AllowAnyHeader()
             .AllowCredentials());
    }
    else
    {
        o.AddDefaultPolicy(p =>
            p.SetIsOriginAllowed(_ => true)
             .AllowAnyMethod()
             .AllowAnyHeader()
             .AllowCredentials());
    }
});

// Rate limiting: per-user partitioned limiter with config-driven limits.
// Admin users get higher rate (AdminRequestsPerMinute), standard users get RequestsPerMinute.
// Health/status endpoints are exempt (no RequireRateLimiting attribute applied).
var rateLimitOptions = new TestController.Api.Security.RateLimitSecurityOptions();
builder.Configuration.GetSection("Security:RateLimit").Bind(rateLimitOptions);

if (rateLimitOptions.Enabled)
{
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Return Retry-After header on 429 responses with RFC 7807 problem+json body
    options.OnRejected = async (context, cancellationToken) =>
    {
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryWindow)
            ? retryWindow
            : TimeSpan.FromSeconds(60);

        context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString();
        context.HttpContext.Response.ContentType = "application/problem+json";
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            type = "https://tools.ietf.org/html/rfc6585#section-4",
            title = "Too Many Requests",
            status = 429,
            detail = $"Rate limit exceeded. Try again after {(int)retryAfter.TotalSeconds} seconds.",
            retryAfterSeconds = (int)retryAfter.TotalSeconds,
        }, cancellationToken);
    };

    options.AddPolicy("telemetry", context =>
    {
        var user = context.User?.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
        var modeProvider = context.RequestServices.GetService<TestController.Api.Security.IAuthenticationModeProvider>();
        var principal = context.User ?? new System.Security.Claims.ClaimsPrincipal();
        var isAdmin = modeProvider?.ResolveRole(principal) == TestController.Api.Security.UserRole.Admin;
        var limit = isAdmin ? rateLimitOptions.AdminRequestsPerMinute : rateLimitOptions.RequestsPerMinute;

        return RateLimitPartition.GetFixedWindowLimiter(user, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    });

    options.AddPolicy("mutation", context =>
    {
        var user = context.User?.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
        var modeProvider = context.RequestServices.GetService<TestController.Api.Security.IAuthenticationModeProvider>();
        var principal = context.User ?? new System.Security.Claims.ClaimsPrincipal();
        var isAdmin = modeProvider?.ResolveRole(principal) == TestController.Api.Security.UserRole.Admin;
        // Mutation: 1/6 of the read rate (min 10)
        var limit = isAdmin
            ? Math.Max(10, rateLimitOptions.AdminRequestsPerMinute / 6)
            : Math.Max(5, rateLimitOptions.RequestsPerMinute / 6);

        return RateLimitPartition.GetFixedWindowLimiter(user, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 2,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    });
});
} // end if (rateLimitOptions.Enabled)

// OpenAPI document for API discovery and tooling
builder.Services.AddOpenApi();

// Health checks: liveness (always OK) + readiness (verifies agent connectivity) + cert expiry
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddCheck<AgentConnectivityHealthCheck>("agents", tags: ["ready"])
    .AddCheck<CertificateExpiryHealthCheck>("tls-cert", tags: ["ready"]);

// OpenTelemetry metrics: custom app meters + Prometheus exporter on /metrics
builder.Services.AddSingleton<AppMetrics>();
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddMeter(AppMetrics.MeterName);
        metrics.AddPrometheusExporter();
    });

var app = builder.Build();

// Fail-fast config validation — errors prevent startup in production
var configValidator = app.Services.GetRequiredService<ConfigValidator>();
var validationResult = configValidator.Validate();
if (!validationResult.IsValid && !app.Environment.IsDevelopment())
{
    var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    startupLog.LogCritical("Configuration validation failed. Errors: {Errors}",
        string.Join("; ", validationResult.Errors));
    throw new InvalidOperationException(
        $"Configuration validation failed: {string.Join("; ", validationResult.Errors)}");
}

app.UseCors();
app.UseMultiIdentitySecurity();
if (rateLimitOptions.Enabled)
{
    app.UseRateLimiter();
}

// Security headers: defense-in-depth against common web attacks
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["X-XSS-Protection"] = "0";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    if (context.Request.IsHttps)
    {
        context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    }
    await next();
});

// Startup validation: warn if CORS allows all origins in non-Development environments
if (allowedOrigins is null or { Length: 0 } && !app.Environment.IsDevelopment())
{
    var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    startupLogger.LogWarning(
        "CORS is configured to allow ALL origins. This is acceptable for same-machine deployments " +
        "but should be restricted via Cors:AllowedOrigins in production environments exposed to networks.");
}

// Global exception handler — logs to crash infrastructure + returns 500 JSON
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetService<IAppLogger>();
        logger?.Error("UnhandledRequest", $"{context.Request.Method} {context.Request.Path}: {ex.Message}", ex);
        CrashDumpHelper.AppendCrashLog($"[RequestException] {context.Request.Method} {context.Request.Path}: {ex}");
        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            error = "Internal server error",
            message = ex.Message,
            timestamp = DateTime.UtcNow,
        }));
    }
});

// Architecture: the WPF controller is the single pipeline-execution engine. When a controller
// proxy URL is configured (co-located deployment), forward web-triggered execution writes
// (trigger / cancel / RBAC retry) to the controller so local commands, rCloud revert and email
// run on the controller node under the controller identity, and the authoritative single-run
// lock is enforced there. Registered only when a proxy is configured; standalone WebApi-only
// deployments keep executing locally.
if (app.Services.GetRequiredService<ControllerProxyService>().IsConfigured)
{
    app.UseMiddleware<ControllerExecutionForwardingMiddleware>();
}

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        // Hashed assets (e.g. /assets/index-B-0ZyQQJ.js) can be cached forever.
        // index.html must never be cached so browsers always fetch fresh bundle refs.
        var path = ctx.Context.Request.Path.Value ?? "";
        if (path.StartsWith("/assets/"))
        {
            ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
        }
        else
        {
            ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        }
    }
});

// Shared API: controllers (execution, watchlist, agents, health, results) + single SignalR hub
app.UseControllerApi("/hubs/controller");

// Health endpoints: /healthz/live (liveness) + /healthz/ready (readiness including agent connectivity)
app.MapHealthChecks("/healthz/live", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live"),
}).AllowAnonymous();
app.MapHealthChecks("/healthz/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
}).AllowAnonymous();

// Prometheus metrics endpoint
app.MapPrometheusScrapingEndpoint("/metrics").AllowAnonymous();

// OpenAPI endpoint
app.MapOpenApi();

// Standalone-only minimal API endpoints (features not in the shared library):
// - WatchList file I/O (import/export/xml/refresh/save)
// - Direct agent gRPC queries (snapshot, health, history, audit, diagnose, register/unregister)
// - Extended execution (trigger-all, trigger-event, retry)
// - Build results export and email reports
app.MapGroup("/api/watchlist").MapWatchListEndpoints().RequireAuthorization(SecurityPolicies.User);
app.MapGroup("/api/agents").MapAgentEndpoints().RequireRateLimiting("telemetry").RequireAuthorization(SecurityPolicies.User);
app.MapGroup("/api/execution").MapExecutionEndpoints().RequireRateLimiting("mutation").RequireAuthorization(SecurityPolicies.User);
app.MapGroup("/api").MapLockEndpoints().RequireAuthorization(SecurityPolicies.User);
// Auth is proxied to the controller (secondary host has no session store); allow anonymous so
// login/guest reach the controller, which performs the real authentication.
app.MapGroup("/api").MapAuthEndpoints().AllowAnonymous();
app.MapGroup("/api/results").MapResultsEndpoints().RequireRateLimiting("telemetry").RequireAuthorization(SecurityPolicies.User);
app.MapGroup("/api/deployment").MapDeploymentEndpoints().RequireRateLimiting("mutation").RequireAuthorization(SecurityPolicies.Admin);
app.MapGroup("/api/tokens").MapTokenManagementEndpoints().RequireRateLimiting("mutation").RequireAuthorization(SecurityPolicies.Admin);

// Client-side error logs ingestion (WebClient AppLogPanel → server logs)
app.MapPost("/api/clientlogs", async (HttpContext ctx, IAppLogger logger) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    logger.Warn("ClientLog", body);
    return Results.Ok();
}).AllowAnonymous();

// SPA fallback
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();
}
catch (Exception ex)
{
    CrashDumpHelper.RecordCrash("TopLevel", ex, isTerminating: true);
    throw;
}

// Expose for WebApplicationFactory<Program> in integration tests
public partial class Program { }
