using System.Windows;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Views;

namespace TestControllerGrpc.ViewModels;

// ?? Results Dashboard: singleton window management ??????????????????
public sealed partial class MainViewModel
{
    private ResultsDashboardWindow? _resultsDashboardWindow;

    [RelayCommand]
    private void OpenResults()
    {
        // Singleton pattern: reuse existing window if still open
        if (_resultsDashboardWindow is not null && _resultsDashboardWindow.IsLoaded)
        {
            _resultsDashboardWindow.Activate();
            if (_resultsDashboardWindow.WindowState == WindowState.Minimized)
                _resultsDashboardWindow.WindowState = WindowState.Normal;
            return;
        }

        _resultsDashboardWindow = new ResultsDashboardWindow(BuildResultsVM);
        _resultsDashboardWindow.Closed += (_, _) => _resultsDashboardWindow = null;
        _resultsDashboardWindow.Show();
    }
}
