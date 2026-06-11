using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.Views;

namespace TestControllerGrpc.ViewModels;

// ── Security: Default-mode banner, Settings popup, User Management popup ──
public sealed partial class MainViewModel
{
    /// <summary>True when RBAC is disabled (Default mode). Drives banner visibility.</summary>
    [ObservableProperty] private bool _isDefaultMode;

    private SettingsWindow? _settingsWindow;
    private UserManagementWindow? _userManagementWindow;

    [RelayCommand]
    private void OpenSettings()
    {
        if (_settingsWindow is not null && _settingsWindow.IsLoaded)
        {
            _settingsWindow.Activate();
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            return;
        }

        _settingsWindow = new SettingsWindow();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Owner = Application.Current.MainWindow;
        _settingsWindow.ShowDialog();
    }

    [RelayCommand]
    private void OpenUserManagement()
    {
        if (_userManagementWindow is not null && _userManagementWindow.IsLoaded)
        {
            _userManagementWindow.Activate();
            if (_userManagementWindow.WindowState == WindowState.Minimized)
                _userManagementWindow.WindowState = WindowState.Normal;
            return;
        }

        _userManagementWindow = new UserManagementWindow();
        _userManagementWindow.Closed += (_, _) => _userManagementWindow = null;
        _userManagementWindow.Owner = Application.Current.MainWindow;
        _userManagementWindow.ShowDialog();
    }

    /// <summary>Called during initialization and on mode changes to refresh IsDefaultMode.</summary>
    private void RefreshDefaultModeState()
    {
        var systemMode = App.Services.GetService<Services.SystemModeClient>();
        IsDefaultMode = systemMode is null || !systemMode.IsSecuredMode;
    }
}
