using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TestController.Dashboard.Composition;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels.Execution;
using TestControllerGrpc.Views;

namespace TestController.Dashboard;

/// <summary>
/// Composition root for the standalone Execution Dashboard.
///
/// Wires:
///   1. Resolves the controller hub URL from --hub=&lt;url&gt; CLI arg or appsettings.json.
///   2. Creates a feed-only <see cref="ExecutionDashboardVM"/> (no in-process services).
///   3. Connects a <see cref="SignalRExecutionFeed"/> to <c>{base}/hubs/controller</c>.
///   4. Starts <see cref="FeedToVmAdapter"/> which translates feed messages into VM mutations.
///   5. Reuses <see cref="ExecutionDashboardWindow"/> from the TestControllerGrpc project
///      so there is zero UI duplication.
/// </summary>
public partial class App : Application
{
    private SignalRExecutionFeed? _feed;
    private FeedToVmAdapter? _adapter;
    private ExecutionDashboardWindow? _window;
    private ILoggerFactory? _loggerFactory;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            // ?? 1. Configuration: --hub=... CLI arg overrides appsettings ??
            var hubUrl = ResolveHubUrl(e.Args);

            // ?? 2. Logging: console + file fallback ??
            _loggerFactory = LoggerFactory.Create(b => b
                .SetMinimumLevel(LogLevel.Information)
                .AddSimpleConsole(o =>
                {
                    o.SingleLine = true;
                    o.TimestampFormat = "HH:mm:ss ";
                }));
            var log = _loggerFactory.CreateLogger<App>();
            log.LogInformation("Starting Execution Dashboard against {Hub}", hubUrl);

            // ?? 3. Build VM in feed-only mode (no in-process services) ??
            var vm = ExecutionDashboardVM.CreateForFeed(Dispatcher);
            vm.ConnectionStatus = "Connecting";

            // ?? 4. Build the feed ??
            _feed = new SignalRExecutionFeed(
                new Uri(hubUrl),
                Environment.UserName,
                Dispatcher,
                _loggerFactory.CreateLogger<SignalRExecutionFeed>());

            // ?? 5. Wire feed ? VM via adapter ??
            _adapter = new FeedToVmAdapter(_feed, vm, _loggerFactory.CreateLogger<FeedToVmAdapter>());
            _adapter.Attach();

            // ?? 6. Show window (reuses TestControllerGrpc’s ExecutionDashboardWindow) ??
            _window = new ExecutionDashboardWindow { DataContext = vm };
            _window.Closed += (_, _) => Shutdown();
            _window.Show();

            // ?? 7. Start the feed (non-fatal if it fails; user will see footer go red) ??
            try
            {
                await _feed.StartAsync();
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Initial connection failed; auto-reconnect will keep retrying");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Dashboard startup failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            _adapter?.Detach();
            if (_feed != null)
            {
                await _feed.StopAsync();
                await _feed.DisposeAsync();
            }
            _loggerFactory?.Dispose();
        }
        catch { /* best effort on exit */ }
        base.OnExit(e);
    }

    /// <summary>
    /// Resolves the hub URL with this priority:
    ///   1. <c>--hub=http://host:port/hubs/controller</c> CLI argument
    ///   2. <c>ControllerHubUrl</c> in appsettings.json next to the executable
    ///   3. Hard-coded fallback for first-run dev convenience
    /// </summary>
    private static string ResolveHubUrl(string[] args)
    {
        const string fallback = "http://localhost:5200/hubs/controller";

        var fromArgs = args.FirstOrDefault(a => a.StartsWith("--hub=", StringComparison.OrdinalIgnoreCase));
        if (fromArgs != null) return fromArgs.Substring("--hub=".Length);

        try
        {
            var cfg = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true)
                .Build();
            var fromCfg = cfg["ControllerHubUrl"];
            if (!string.IsNullOrWhiteSpace(fromCfg)) return fromCfg!;
        }
        catch { /* fall through */ }

        return fallback;
    }
}
