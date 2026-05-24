using System.Diagnostics;
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
builder.Services.Configure<AgentKestrelOptions>(
    builder.Configuration.GetSection("AgentKestrel"));
builder.Services.Configure<CommandPolicySettings>(
    builder.Configuration.GetSection("CommandPolicy"));
builder.Services.Configure<NotificationSettings>(
    builder.Configuration.GetSection("NotificationSettings"));
builder.Services.Configure<AuditSettings>(
    builder.Configuration.GetSection("AuditSettings"));

builder.WebHost.ConfigureKestrel(options =>
{
    var port = builder.Configuration.GetValue("AgentSettings:GrpcPort", 5200);
    var kestrelOpts = builder.Configuration.GetSection("AgentKestrel").Get<AgentKestrelOptions>() ?? new();

    options.ListenAnyIP(port, o => o.Protocols = HttpProtocols.Http2);
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(kestrelOpts.KeepAliveTimeoutMinutes);

    if (kestrelOpts.DisableMinRequestBodyDataRate)
        options.Limits.MinRequestBodyDataRate = null;
    if (kestrelOpts.DisableMinResponseDataRate)
        options.Limits.MinResponseDataRate = null;
});

// ── Core services ──────────────────────────────────────────────────────
builder.Services.AddSingleton<EventBroadcaster>();
builder.Services.AddSingleton<ExecutionTracker>();
builder.Services.AddSingleton<AuditLogger>();
builder.Services.AddSingleton(sp =>
    new CommandPolicyEvaluator(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CommandPolicySettings>>().Value));
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
builder.Services.AddHostedService<GrpcListenerWatchdog>();
var app = builder.Build();

app.MapGrpcService<TestAgentGrpcService>();
app.MapGet("/", () => "TestAgent gRPC service is running.");
app.MapGet("/health", (CommandExecutor executor) => Results.Ok(new
{
    status = "healthy",
    agent = executor.CurrentState.ToString(),
    pid = Environment.ProcessId,
    uptime = (DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).ToString(@"d\.hh\:mm\:ss"),
}));

// ── Startup validation: warn if running plaintext HTTP/2 in production ─
var kestrelConfig = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentKestrelOptions>>().Value;
if (kestrelConfig.WarnOnPlaintextHttp2 && !app.Environment.IsDevelopment())
{
    var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    startupLogger.LogWarning(
        "Agent is listening on plaintext HTTP/2 (no TLS). " +
        "This is acceptable for isolated test networks but should not be used in untrusted environments. " +
        "Set AgentKestrel:WarnOnPlaintextHttp2 = false to suppress this warning.");
}

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

// Wait for EITHER the UI thread to exit OR the host to signal shutdown.
// Previously, uiThread.Join() meant closing the tray icon would unconditionally
// kill the gRPC host. Now, the host stays alive (serving gRPC) even if the
// tray app exits — the process only terminates when IHostApplicationLifetime
// requests shutdown (e.g., from GrpcListenerWatchdog or an explicit stop).
await Task.WhenAny(
    hostTask,
    Task.Run(() => uiThread.Join()));

// If the host task hasn't completed yet (UI exited first), check if agent is busy.
// Only stop the host if NOT actively executing — this prevents pipeline interruption
// from a simple tray-icon close.
if (!hostTask.IsCompleted)
{
    if (cmdExec.CurrentState == AgentState.Running)
    {
        var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Shutdown");
        startupLogger.LogWarning(
            "Tray application exited while agent is executing a command. " +
            "Host will remain alive until execution completes or host shutdown is requested.");
        CrashDumpHelper.AppendCrashLog(
            "[Lifecycle] Tray app exited during active execution — host kept alive");

        // Wait for the host to stop naturally (via IHostApplicationLifetime)
        await hostTask;
    }
    else
    {
        await app.StopAsync();
        await hostTask;
    }
}
else
{
    // Host stopped first (explicit shutdown or listener watchdog)
    await hostTask;
}
}
catch (Exception ex)
{
    CrashDumpHelper.RecordCrash("TopLevel", ex, isTerminating: true);
    throw;
}
