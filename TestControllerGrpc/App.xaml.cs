using System.Windows;
using System.IO;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TestController.Api;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels;

using TestControllerGrpc.Views.Dialogs;

namespace TestControllerGrpc;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    private IHost? _host;
    private static Mutex? _singleInstanceMutex;
    private readonly CancellationTokenSource _appShutdownCts = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        // Allow gRPC over plain HTTP/2 (without TLS) for local/intranet agent communication.
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        // Install crash capture infrastructure FIRST — before any async work
        // can escape into a native callback and terminate the process.
        var logDir = AppLogger.DefaultLogDirectory;
        CrashDumpHelper.InstallGlobalHandlers("controller", logDir);

        // WPF-specific handler for exceptions on the Dispatcher thread.
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Single instance enforcement
        const string mutexName = "Global\\TestControllerGrpc_SingleInstance";
        _singleInstanceMutex = new Mutex(true, mutexName, out bool isNew);

        if (!isNew)
        {
            var answer = ThemedMessageBox.Show(
                "TestController is already running.\n\n" +
                "YES = Kill old instance and start fresh\n" +
                "NO = Cancel (switch to existing window manually)",
                "Already Running",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (answer == MessageBoxResult.Yes)
            {
                KillOtherInstances();
                _singleInstanceMutex.Dispose();
                Thread.Sleep(3000);
                _singleInstanceMutex = new Mutex(true, mutexName, out isNew);
                if (!isNew)
                {
                    ThemedMessageBox.Show(
                        "Old process still running. Wait a moment and retry.",
                        "Startup Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    Shutdown(1);
                    return;
                }
            }
            else
            {
                Shutdown(0);
                return;
            }
        }

        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((ctx, cfg) =>
            {
                cfg.SetBasePath(AppContext.BaseDirectory);
                cfg.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
            })
            .ConfigureLogging(log =>
            {
                log.SetMinimumLevel(LogLevel.Information);
            })
            .ConfigureServices((ctx, services) =>
            {
                // RBAC feature (identity, authorization, audit, persistence)
                services.AddRbacFeature(ctx.Configuration);

                services.AddSingleton<IEventAggregator, EventAggregator>();
                services.AddSingleton<IVocabularyMonitor, VocabularyMonitor>();
                services.AddSingleton<IAgentGrpcDispatcher, AgentGrpcDispatcher>();
                services.AddSingleton(sp =>
                {
                    var cfg = sp.GetRequiredService<IConfiguration>();
                    var logDir = cfg.GetValue<string>("Logging:LogDirectory")
                        ?? cfg.GetValue<string>("LogDirectory")
                        ?? AppLogger.DefaultLogDirectory;
                    var sessionsFile = Path.Combine(logDir, "session-snapshots.json");
                    var events = sp.GetRequiredService<IEventAggregator>();
                    return new ExecutionSessionManager(events, sessionsFile);
                });
                services.AddSingleton(sp =>
                {
                    var cfg = sp.GetRequiredService<IConfiguration>();
                    var logDir = cfg.GetValue<string>("Logging:LogDirectory")
                        ?? cfg.GetValue<string>("LogDirectory")
                        ?? AppLogger.DefaultLogDirectory;
                    var lockFile = Path.Combine(logDir, "agent-locks.json");
                    return new AgentLockManager(lockFile);
                });
                services.AddSingleton<IActionPipelineExecutor, ActionPipelineExecutor>();
                services.AddSingleton<IFileWatcherManager, FileWatcherManager>();
                services.AddSingleton<IWatchListXmlParser, WatchListXmlParserService>();
                services.AddHostedService<ControllerHostedService>();
                services.AddHostedService<ControllerGrpcServerHost>();
                services.AddHostedService<ControllerWebApiHost>();

                // Application logger (file + in-memory ring buffer)
                services.AddSingleton<IAppLogger>(sp =>
                {
                    var cfg = sp.GetRequiredService<IConfiguration>();
                    var logDir = cfg.GetValue<string>("Logging:LogDirectory")
                        ?? cfg.GetValue<string>("LogDirectory")
                        ?? AppLogger.DefaultLogDirectory;
                    return new AppLogger("controller", logDir);
                });

                services.AddSingleton<MainViewModel>();
                services.AddSingleton<ExecutionHistoryPanelVM>();

                // Phase 0.5: SystemMode client + Settings ViewModel
                services.AddHttpClient("SystemMode", client =>
                {
                    client.BaseAddress = new Uri("http://localhost:5000");
                });
                services.AddSingleton<SystemModeClient>();
                services.AddSingleton<ViewModels.Settings.SecurityModeViewModel>();

                // Health threshold settings (operator-configurable)
                services.AddSingleton(sp =>
                {
                    var config = sp.GetRequiredService<IConfiguration>();
                    var settings = new TestControllerGrpc.ViewModels.HealthThresholdSettings();
                    config.GetSection("HealthThresholds").Bind(settings);
                    return settings;
                });

                // Build Results services
                services.AddSingleton<TrxResultsParser>();
                services.AddSingleton(sp =>
                {
                    var config = sp.GetRequiredService<IConfiguration>();
                    var rc = new BuildResultsConfig();
                    config.GetSection("BuildResults").Bind(rc);
                    return rc;
                });
                services.AddSingleton<BuildResultsAggregator>();
                services.AddSingleton<BuildReportHtmlGenerator>();
                services.AddSingleton<FailurePatternAnalyzer>();
                services.AddSingleton<ExecutionLogCorrelator>();
                services.AddSingleton<BuildResultsViewModel>();
            })
            .Build();

        Services = _host.Services;

        var mainWindow = new Views.MainWindow();
        mainWindow.Show();

        // Observe host startup so any RpcException / hosted-service failure
        // is logged instead of escaping as an unobserved task exception
        // (which the OS would surface as 0xC000041D and kill the process).
        _ = _host.StartAsync().ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception is not null)
            {
                CrashDumpHelper.RecordCrash("HostStart", t.Exception);
                Dispatcher.BeginInvoke(new Action(() =>
                    ThemedMessageBox.Show(
                        "The application host failed to start. See crash log:\n" + CrashDumpHelper.CrashLogPath,
                        "Startup error", MessageBoxButton.OK, MessageBoxImage.Error)));
            }
        }, TaskScheduler.Default);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashDumpHelper.RecordCrash("DispatcherUnhandled", e.Exception);

        // Keep the app alive for known-recoverable categories. RpcException is
        // routinely raised by transient agent/server disconnects and must NOT
        // be allowed to tear down the WPF host.
        if (CrashDumpHelper.IsRecoverable(e.Exception))
        {
            e.Handled = true;
            return;
        }

        ThemedMessageBox.Show(
            $"Unhandled UI exception:\n\n{e.Exception.GetType().Name}: {e.Exception.Message}\n\n" +
            $"Details: {CrashDumpHelper.CrashLogPath}",
            "Unhandled Exception", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _appShutdownCts.Cancel();

            var vm = Services?.GetService<MainViewModel>();
            vm?.CancelAllPipelines();

            _host?.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        catch { }
        finally
        {
            _host?.Dispose();
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        base.OnExit(e);
    }

    private static void KillOtherInstances()
    {
        var myPid = Environment.ProcessId;
        var myName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(myName))
        {
            if (p.Id != myPid)
            {
                try { p.Kill(entireProcessTree: true); p.WaitForExit(5000); }
                catch { }
            }
        }
    }
}
