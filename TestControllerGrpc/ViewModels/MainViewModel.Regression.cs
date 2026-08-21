using System.Windows;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.ViewModels.Regression;
using TestControllerGrpc.Views.Regression;

namespace TestControllerGrpc.ViewModels;

// ── Regression tab: singleton window management ───────────────────
public sealed partial class MainViewModel
{
    private RegressionWindow? _regressionWindow;

    [RelayCommand]
    private void OpenRegression()
    {
        if (_regressionWindow is not null && _regressionWindow.IsLoaded)
        {
            _regressionWindow.Activate();
            if (_regressionWindow.WindowState == WindowState.Minimized)
                _regressionWindow.WindowState = WindowState.Normal;
            return;
        }

        var vm = App.Services.GetService(typeof(RegressionViewModel)) as RegressionViewModel;
        if (vm is null) return;

        _regressionWindow = new RegressionWindow(vm);
        _regressionWindow.Closed += (_, _) => _regressionWindow = null;
        _regressionWindow.Show();
    }
}
