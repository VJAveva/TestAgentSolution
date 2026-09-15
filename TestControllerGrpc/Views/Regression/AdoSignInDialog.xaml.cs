using System.Windows;
using TestControllerGrpc.ViewModels.Regression;

namespace TestControllerGrpc.Views.Regression;

public partial class AdoSignInDialog : Window
{
    public AdoSignInDialog(AdoSignInViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        // The rebuild runs for minutes-to-hours; staying modal would freeze the rest of the app while it does.
        vm.RebuildStarted += OnRebuildStarted;
        Closed += (_, _) => vm.RebuildStarted -= OnRebuildStarted;
    }

    private void OnRebuildStarted(object? sender, System.EventArgs e) => Close();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
