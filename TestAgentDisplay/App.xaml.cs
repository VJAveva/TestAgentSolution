using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TestAgentDisplay.Services;
using TestAgentDisplay.ViewModels;

namespace TestAgentDisplay;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var sc = new ServiceCollection();
        sc.AddSingleton<IDisplayLog, DisplayLog>();
        sc.AddSingleton<AgentConnectionManager>();
        sc.AddSingleton<AuditTimelineViewModel>();
        sc.AddSingleton<MainViewModel>();
        Services = sc.BuildServiceProvider();

        Services.GetRequiredService<IDisplayLog>().Info("App", "TestAgentDisplay started.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Services is IDisposable d)
            d.Dispose();
        base.OnExit(e);
    }
}
