using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using TestAgentGrpc;
using TestAgentGrpc.Clients;
using TestAgentGrpc.Services;
using TestAgentGrpc.UI;
using TestControllerGrpc.Services;

// ── Crash capture — must be first so nothing escapes unglogged ─────────
var logDir = AppLogger.DefaultLogDirectory;
CrashDumpHelper.InstallGlobalHandlers("agent", logDir);

// ── Single-instance guard (Global\ prefix works across all Windows sessions) ──
using var mutex = new Mutex(true, @"Global\TestAgentGrpc-SingleInstance", out bool createdNew);
if (!createdNew)
{
    const string msg = "Another instance of TestAgentGrpc is already running. This instance will exit.";
    WriteEventLog(msg, EventLogEntryType.Error);
    Console.Error.WriteLine(msg);
    // Also show a message box when running interactively (no-op when run as service)
    if (Environment.UserInteractive)
    {
        MessageBox.Show(msg, "TestAgent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
    Environment.Exit(2);
    return;
}

// ── Pre-flight port check ──────────────────────────────────────────────
var preflightConfig = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();
var grpcPort = preflightConfig.GetValue("AgentSettings:GrpcPort", 5200);

if (!TryReservePort(grpcPort, out string portConflictDetails))
{
    // Try fallback ports if configured
    var allowFallback = preflightConfig.GetValue("AgentSettings:AllowPortFallback", false);
    var fallbackPorts = preflightConfig.GetSection("AgentSettings:FallbackPorts").Get<int[]>() ?? [];
    var resolved = false;

    if (allowFallback && fallbackPorts.Length > 0)
    {
        foreach (var fallback in fallbackPorts)
        {
            if (TryReservePort(fallback, out _))
            {
                var warnMsg = $"Primary port {grpcPort} unavailable ({portConflictDetails}); using fallback port {fallback}.";
                WriteEventLog(warnMsg, EventLogEntryType.Warning);
                Console.WriteLine(warnMsg);
                grpcPort = fallback;
                resolved = true;
                break;
            }
        }
    }

    if (!resolved)
    {
        var msg = $"Cannot start: port {grpcPort} is unavailable. {portConflictDetails}";
        WriteEventLog(msg, EventLogEntryType.Error);
        Console.Error.WriteLine(msg);
        CrashDumpHelper.AppendCrashLog($"[Startup] Port conflict: {msg}");
        if (Environment.UserInteractive)
        {
            MessageBox.Show(msg, "TestAgent \u2014 Port Conflict", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        Environment.Exit(3);
        return;
    }
}

// Override the port in environment so builder picks up the (possibly fallback) value
Environment.SetEnvironmentVariable("ASPNETCORE_AgentSettings__GrpcPort", grpcPort.ToString());

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

// ── Graceful shutdown configuration ────────────────────────────────────
builder.Services.Configure<HostOptions>(opts =>
{
    opts.ShutdownTimeout = TimeSpan.FromSeconds(30);
    opts.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
});

builder.WebHost.ConfigureKestrel(options =>
{
    var port = builder.Configuration.GetValue("AgentSettings:GrpcPort", 5200);
    var kestrelOpts = builder.Configuration.GetSection("AgentKestrel").Get<AgentKestrelOptions>() ?? new();

    // Plaintext HTTP/2 listener (always active unless TLS-only)
    if (!kestrelOpts.EnableTls || kestrelOpts.TlsPort != port)
    {
        options.ListenAnyIP(port, o => o.Protocols = HttpProtocols.Http2);
    }

    // TLS HTTP/2 listener (when enabled)
    if (kestrelOpts.EnableTls)
    {
        var cert = LoadServerCertificate(kestrelOpts);
        options.ListenAnyIP(kestrelOpts.TlsPort, listenOpts =>
        {
            listenOpts.Protocols = HttpProtocols.Http2;
            listenOpts.UseHttps(httpsOpts =>
            {
                httpsOpts.ServerCertificate = cert;
                httpsOpts.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                if (kestrelOpts.RequireClientCertificate)
                {
                    httpsOpts.ClientCertificateMode = Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.RequireCertificate;
                }
            });
        });
    }

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
builder.Services.AddSingleton(sp =>
    new EnhancedCommandPolicyEvaluator(
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CommandPolicySettings>>().Value,
        sp.GetRequiredService<ILogger<EnhancedCommandPolicyEvaluator>>()));
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

// ── Helper: Load server certificate for TLS ────────────────────────────
static X509Certificate2 LoadServerCertificate(AgentKestrelOptions opts)
{
    // Prefer PFX file if specified
    if (!string.IsNullOrWhiteSpace(opts.CertFilePath))
    {
        if (!File.Exists(opts.CertFilePath))
            throw new FileNotFoundException($"TLS certificate file not found: {opts.CertFilePath}");

        return string.IsNullOrEmpty(opts.CertPassword)
            ? X509CertificateLoader.LoadPkcs12FromFile(opts.CertFilePath, null)
            : X509CertificateLoader.LoadPkcs12FromFile(opts.CertFilePath, opts.CertPassword);
    }

    // Fall back to cert store by thumbprint
    if (string.IsNullOrWhiteSpace(opts.CertThumbprint))
        throw new InvalidOperationException(
            "TLS is enabled but neither CertFilePath nor CertThumbprint is configured in AgentKestrel settings.");

    using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
    store.Open(OpenFlags.ReadOnly);
    var certs = store.Certificates.Find(X509FindType.FindByThumbprint, opts.CertThumbprint, validOnly: false);

    if (certs.Count == 0)
        throw new InvalidOperationException(
            $"Certificate with thumbprint '{opts.CertThumbprint}' not found in LocalMachine\\My store.");

    return certs[0];
}

// ── Helper: Pre-flight port availability check ─────────────────────────
static bool TryReservePort(int port, out string details)
{
    try
    {
        using var listener = new TcpListener(IPAddress.IPv6Any, port);
        listener.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
        listener.Start();
        listener.Stop();
        details = "OK";
        return true;
    }
    catch (SocketException)
    {
        details = FindPortHolder(port);
        return false;
    }
}

// ── Helper: Identify which process holds a port ────────────────────────
static string FindPortHolder(int port)
{
    try
    {
        var psi = new ProcessStartInfo("netstat", "-ano")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi);
        if (proc is null) return "(could not start netstat)";

        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(10_000);

        var line = output.Split('\n')
            .FirstOrDefault(l => l.Contains($":{port}") && l.Contains("LISTENING"));

        if (line is null) return $"port {port} in use (no LISTENING entry found — may be in TIME_WAIT)";

        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var pidStr = tokens.LastOrDefault();
        if (pidStr is null || !int.TryParse(pidStr, out var pid))
            return $"holder PID unknown ({line.Trim()})";

        try
        {
            var p = Process.GetProcessById(pid);
            return $"held by {p.ProcessName} (PID {pid})";
        }
        catch
        {
            return $"held by PID {pid} (process no longer exists — socket in TIME_WAIT)";
        }
    }
    catch (Exception ex)
    {
        return $"diagnostic failed: {ex.Message}";
    }
}

// ── Helper: Write to Windows Event Log (best-effort) ───────────────────
static void WriteEventLog(string message, EventLogEntryType entryType)
{
    try
    {
        const string source = "TestAgentGrpc";
        if (!EventLog.SourceExists(source))
        {
            // Creating event sources requires admin; if it fails, just skip.
            try { EventLog.CreateEventSource(source, "Application"); }
            catch { /* best effort */ }
        }
        EventLog.WriteEntry(source, message, entryType);
    }
    catch
    {
        // Event log write is best-effort; don't crash because of it.
    }
}
