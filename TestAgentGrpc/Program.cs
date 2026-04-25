using Microsoft.AspNetCore.Server.Kestrel.Core;
using TestAgentGrpc;
using TestAgentGrpc.Clients;
using TestAgentGrpc.Services;
using TestAgentGrpc.UI;

// ── Single-instance guard ──────────────────────────────────────────────
using var mutex = new Mutex(true, "TestAgentGrpc", out bool createdNew);
if (!createdNew)
{
    MessageBox.Show("TestAgent is already running.", "TestAgent",
        MessageBoxButtons.OK, MessageBoxIcon.Information);
    return;
}

// ── Build host ─────────────────────────────────────────────────────────
var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AgentSettings>(
    builder.Configuration.GetSection("AgentSettings"));
builder.Services.Configure<NotificationSettings>(
    builder.Configuration.GetSection("NotificationSettings"));
builder.Services.Configure<AuditSettings>(
    builder.Configuration.GetSection("AuditSettings"));

builder.WebHost.ConfigureKestrel(options =>
{
    var port = builder.Configuration.GetValue("AgentSettings:GrpcPort", 5200);
    options.ListenAnyIP(port, o => o.Protocols = HttpProtocols.Http2);
    // Allow long-running gRPC streams (test executions can take 2-4+ hours).
    // Default KeepAliveTimeout (130s) and MinDataRate (240 bytes/sec with 5s grace)
    // kill connections during long installs/compiles that produce no stdout for
    // extended periods. The amount of initial output determines how long the
    // connection survives — typically ~1 hour, which matches the reported failures.
    options.Limits.KeepAliveTimeout = TimeSpan.FromHours(4);
    options.Limits.MinRequestBodyDataRate = null;
    options.Limits.MinResponseDataRate = null;
});

// ── Core services ──────────────────────────────────────────────────────
builder.Services.AddSingleton<EventBroadcaster>();
builder.Services.AddSingleton<ExecutionTracker>();
builder.Services.AddSingleton<AuditLogger>();
builder.Services.AddSingleton<CommandExecutor>();
builder.Services.AddSingleton<SystemMetricsCollector>();
builder.Services.AddSingleton<TestControllerClient>();
builder.Services.AddSingleton<ConnectionHealthMonitor>();

// ── gRPC server ────────────────────────────────────────────────────────
builder.Services.AddGrpc(options =>
{
    options.MaxReceiveMessageSize = 16 * 1024 * 1024;
    options.MaxSendMessageSize    = 16 * 1024 * 1024;
    options.EnableDetailedErrors  = builder.Environment.IsDevelopment();
});

// ── Background services ───────────────────────────────────────────────
builder.Services.AddHostedService(sp => sp.GetRequiredService<AuditLogger>());
builder.Services.AddSingleton<AgentLifecycleService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentLifecycleService>());

var app = builder.Build();

app.MapGrpcService<TestAgentGrpcService>();
app.MapGet("/", () => "TestAgent gRPC service is running.");

// ── Run host on background thread, WinForms on STA main thread ────────
var hostTask = app.RunAsync();

Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);

var trayApp = new TrayApplicationContext(
    app.Services.GetRequiredService<CommandExecutor>(),
    app.Services.GetRequiredService<EventBroadcaster>(),
    app.Services.GetRequiredService<ExecutionTracker>(),
    app.Services.GetRequiredService<SystemMetricsCollector>(),
    app.Services.GetRequiredService<ConnectionHealthMonitor>(),
    app.Services.GetRequiredService<TestControllerClient>(),
    app.Services.GetRequiredService<AgentLifecycleService>(),
    app.Services.GetRequiredService<IHostApplicationLifetime>(),
    app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentSettings>>(),
    app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<NotificationSettings>>(),
    app.Services.GetRequiredService<ILogger<TrayApplicationContext>>());

Application.Run(trayApp);

// Graceful shutdown
await app.StopAsync();
await hostTask;
