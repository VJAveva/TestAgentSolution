using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace TestControllerGrpc.Tests.Ui;

/// <summary>
/// One STA thread that owns the process's only <see cref="Application"/> (a hard WPF rule) and the app theme.
/// Every WPF test body runs on its dispatcher, so test classes can run in parallel without racing to create it.
/// </summary>
internal static class WpfTestHost
{
    private static readonly Lazy<Dispatcher> Ui = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    public static T Run<T>(Func<T> body) => Ui.Value.Invoke(body);

    public static void Run(Action body) => Ui.Value.Invoke(body);

    private static Dispatcher Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                foreach (var path in new[]
                {
                    "Themes/DarkTheme.xaml",
                    "Views/Styles/DesignTokens.xaml",
                    "Views/Styles/Typography.xaml",
                    "Views/Styles/ControlStyles.xaml",
                    "Views/Styles/ExecutionDashboardStyles.xaml",
                    "Views/Styles/SpecTokens.xaml",
                    "Views/Styles/TreeViewSpec.xaml",
                })
                {
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri($"pack://application:,,,/TestControllerGrpc;component/{path}", UriKind.Absolute),
                    });
                }
                ready.SetResult(Dispatcher.CurrentDispatcher);
            }
            catch (Exception ex)
            {
                ready.SetException(ex);
                return;
            }
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "WpfTestHost" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    }
}
