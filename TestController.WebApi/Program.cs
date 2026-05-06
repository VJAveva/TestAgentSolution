using System.Text.Json;
using System.Text.Json.Serialization;
using TestController.Api;
using TestController.WebApi.Endpoints;
using TestController.WebApi.Services;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddSingleton<AgentGrpcClientManager>();
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<WatchListFileService>();
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

// Shared API library: controllers for execution, watchlist, agents, health, results + SignalR hub + bridge
builder.Services.AddControllerApi()
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

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.SetIsOriginAllowed(_ => true)
     .AllowAnyMethod()
     .AllowAnyHeader()
     .AllowCredentials()));

var app = builder.Build();

app.UseCors();
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

// Standalone-only minimal API endpoints (features not in the shared library):
// - WatchList file I/O (import/export/xml/refresh/save)
// - Direct agent gRPC queries (snapshot, health, history, audit, diagnose, register/unregister)
// - Extended execution (trigger-all, trigger-event, retry)
// - Build results export and email reports
app.MapGroup("/api/watchlist").MapWatchListEndpoints();
app.MapGroup("/api/agents").MapAgentEndpoints();
app.MapGroup("/api/execution").MapExecutionEndpoints();
app.MapGroup("/api/results").MapResultsEndpoints();

// Client-side error logs ingestion (WebClient AppLogPanel → server logs)
app.MapPost("/api/clientlogs", (HttpContext ctx, IAppLogger logger) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = reader.ReadToEndAsync().GetAwaiter().GetResult();
    logger.Warn("ClientLog", body);
    return Results.Ok();
});

// SPA fallback
app.MapFallbackToFile("index.html");

app.Run();

// Expose for WebApplicationFactory<Program> in integration tests
public partial class Program { }
