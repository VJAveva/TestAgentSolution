using Microsoft.AspNetCore.Server.Kestrel.Core;
using TestAgentGrpc;
using TestAgentGrpc.Clients;
using TestAgentGrpc.Services;
using TestAgentGrpc.UI;
using TestControllerGrpc.Services;

// ── Crash capture — must be first so nothing escapes unglogged ─────────
var logDir = AppLogger.DefaultLogDirectory;
CrashDumpHelper.InstallGlobalHandlers("agent", logDir);

// ── Single-instance guard ──────────────────────────────────────────────
using var mutex = new Mutex(true, "TestAgentGrpc", out bool createdNew);
if (!createdNew)
{
    MessageBox.Show("TestAgent is already running.", "TestAgent",
        MessageBoxButtons.OK, MessageBoxIcon.Information);
    return;
}

// ── Build host ─────────────────────────────────────────────────────────
try
{
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
builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentLifecycleService>());builder.Services.AddHostedService<StuckExecutionWatchdog>();
var app = builder.Build();

app.MapGrpcService<TestAgentGrpcService>();
app.MapGet("/", () => "TestAgent gRPC service is running.");

// ── Run host on background thread, WinForms on dedicated STA thread ───
// Top-level statements compile to async Main which the CLR runs on an MTA
// thread (STAThread is ignored on async entry points). OLE operations
// (Clipboard, SaveFileDialog) require STA, so we spin up a dedicated
// STA thread for the WinForms message loop.
var hostTask = app.RunAsync();

// Resolve services on the main thread (DI is thread-safe).
var cmdExec     = app.Services.GetRequiredService<CommandExecutor>();
var broadcaster = app.Services.GetRequiredService<EventBroadcaster>();
var tracker     = app.Services.GetRequiredService<ExecutionTracker>();
var metrics     = app.Services.GetRequiredService<SystemMetricsCollector>();
var healthMon   = app.Services.GetRequiredService<ConnectionHealthMonitor>();
var ctrlClient  = app.Services.GetRequiredService<TestControllerClient>();
var lifecycle   = app.Services.GetRequiredService<AgentLifecycleService>();
var appLifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
var agentOpts   = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentSettings>>();
var notifOpts   = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<NotificationSettings>>();
var trayLogger  = app.Services.GetRequiredService<ILogger<TrayApplicationContext>>();

var uiThread = new Thread(() =>
{
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);

    var trayApp = new TrayApplicationContext(
        cmdExec, broadcaster, tracker, metrics, healthMon,
        ctrlClient, lifecycle, appLifetime, agentOpts, notifOpts, trayLogger);

    Application.Run(trayApp);
});
uiThread.SetApartmentState(ApartmentState.STA);
uiThread.IsBackground = true;
uiThread.Name = "WinFormsUI";
uiThread.Start();
uiThread.Join();

// Graceful shutdown
await app.StopAsync();
await hostTask;
}
catch (Exception ex)
{
    CrashDumpHelper.RecordCrash("TopLevel", ex, isTerminating: true);
    throw;
}
