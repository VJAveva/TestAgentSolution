using System.Windows;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    private IHost? _host;
    private static Mutex? _singleInstanceMutex;
    private readonly CancellationTokenSource _appShutdownCts = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single instance enforcement
        const string mutexName = "Global\\TestControllerGrpc_SingleInstance";
        _singleInstanceMutex = new Mutex(true, mutexName, out bool isNew);

        if (!isNew)
        {
            var answer = MessageBox.Show(
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
                    MessageBox.Show(
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
            .ConfigureServices(services =>
            {
                services.AddSingleton<IEventAggregator, EventAggregator>();
                services.AddSingleton<IVocabularyMonitor, VocabularyMonitor>();
                services.AddSingleton<IAgentGrpcDispatcher, AgentGrpcDispatcher>();
                services.AddSingleton<ExecutionSessionManager>();
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
                    var logDir = cfg.GetValue<string>("LogDirectory")
                        ?? Path.Combine(AppContext.BaseDirectory, "Logs");
                    return new AppLogger("controller", logDir);
                });

                services.AddSingleton<MainViewModel>();

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
                services.AddSingleton<BuildResultsViewModel>();
            })
            .Build();

        Services = _host.Services;
        _ = _host.StartAsync();
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
