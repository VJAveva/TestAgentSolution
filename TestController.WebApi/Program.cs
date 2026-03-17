using TestController.WebApi.Endpoints;
using TestController.WebApi.Hubs;
using TestController.WebApi.Services;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

var builder = WebApplication.CreateBuilder(args);

// Shared services from Core
builder.Services.AddSingleton<TrxResultsParser>();
builder.Services.AddSingleton(sp =>
{
    var rc = new BuildResultsConfig();
    builder.Configuration.GetSection("BuildResults").Bind(rc);
    return rc;
});
builder.Services.AddSingleton<BuildResultsAggregator>();
builder.Services.AddSingleton<BuildTrendAnalyzer>();
builder.Services.AddSingleton<ConsecutiveFailureDetector>();
builder.Services.AddSingleton<BuildReportHtmlGenerator>();
builder.Services.AddSingleton<ExecutionSessionManager>();
builder.Services.AddSingleton<IAppLogger>(sp =>
    new AppLogger("webapi", builder.Configuration["LogDirectory"] ?? Path.Combine(AppContext.BaseDirectory, "Logs")));

// Web API specific services
builder.Services.AddSingleton<AgentGrpcClientManager>();
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<WatchListFileService>();
builder.Services.AddHostedService<SignalRBroadcastService>();

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
app.UseStaticFiles();

// API routes
app.MapGroup("/api/watchlist").MapWatchListEndpoints();
app.MapGroup("/api/agents").MapAgentEndpoints();
app.MapGroup("/api/execution").MapExecutionEndpoints();
app.MapGroup("/api/results").MapResultsEndpoints();
app.MapHub<LiveHub>("/hub/live");

// SPA fallback
app.MapFallbackToFile("index.html");

app.Run();
