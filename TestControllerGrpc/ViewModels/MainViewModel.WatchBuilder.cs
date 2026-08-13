using System.Windows;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.ViewModels.WatchBuilder;
using TestControllerGrpc.Views.WatchBuilder;

namespace TestControllerGrpc.ViewModels;

// ── WatchItem Builder: singleton window management (Tier3 §5) ─────────
public sealed partial class MainViewModel
{
    private WatchBuilderWindow? _watchBuilderWindow;

    [RelayCommand]
    private void OpenWatchBuilder()
    {
        if (_watchBuilderWindow is not null && _watchBuilderWindow.IsLoaded)
        {
            _watchBuilderWindow.Activate();
            if (_watchBuilderWindow.WindowState == WindowState.Minimized)
                _watchBuilderWindow.WindowState = WindowState.Normal;
            return;
        }

        var vm = App.Services.GetService(typeof(WatchBuilderViewModel)) as WatchBuilderViewModel;
        if (vm is null) return;

        _watchBuilderWindow = new WatchBuilderWindow(vm);
        _watchBuilderWindow.Closed += (_, _) => _watchBuilderWindow = null;
        _watchBuilderWindow.Show();
    }
}
