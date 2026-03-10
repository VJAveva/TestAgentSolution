using System.Windows;
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

    protected override void OnStartup(StartupEventArgs e)
    {
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
                services.AddSingleton<VocabularyMonitor>();
                services.AddSingleton<AgentGrpcDispatcher>();
                services.AddSingleton<ExecutionSessionManager>();
                services.AddSingleton<ActionPipelineExecutor>();
                services.AddSingleton<FileWatcherManager>();
                services.AddHostedService<ControllerHostedService>();
                services.AddHostedService<ControllerGrpcServerHost>();

                // Application logger (file + in-memory ring buffer)
                services.AddSingleton<IAppLogger>(sp =>
                    new AppLogger("controller", @"C:\TestAgentSolution\Logs"));

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
                services.AddSingleton<BuildResultsViewModel>();
            })
            .Build();

        Services = _host.Services;
        _ = _host.StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.StopAsync().GetAwaiter().GetResult();
        _host?.Dispose();
        base.OnExit(e);
    }
}
