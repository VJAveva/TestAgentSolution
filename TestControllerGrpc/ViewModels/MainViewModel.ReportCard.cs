using System.Windows;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.ViewModels.Results;
using TestControllerGrpc.Views.Results;

namespace TestControllerGrpc.ViewModels;

// ── Build Report Card: singleton window management ───────────────────
public sealed partial class MainViewModel
{
    private BuildReportCardWindow? _reportCardWindow;

    [RelayCommand]
    private void OpenReportCard()
    {
        if (_reportCardWindow is not null && _reportCardWindow.IsLoaded)
        {
            _reportCardWindow.Activate();
            if (_reportCardWindow.WindowState == WindowState.Minimized)
                _reportCardWindow.WindowState = WindowState.Normal;
            return;
        }

        var vm = App.Services.GetService(typeof(BuildReportCardViewModel)) as BuildReportCardViewModel;
        if (vm is null) return;

        _reportCardWindow = new BuildReportCardWindow(vm);
        _reportCardWindow.Closed += (_, _) => _reportCardWindow = null;
        _reportCardWindow.Show();
    }
}
