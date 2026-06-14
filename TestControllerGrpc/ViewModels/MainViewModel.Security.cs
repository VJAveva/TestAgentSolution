using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.Views;
using TestControllerGrpc.Views.Login;

namespace TestControllerGrpc.ViewModels;

// ── Security: Default-mode banner, Settings popup, User Management popup ──
public sealed partial class MainViewModel
{
    /// <summary>True when RBAC is disabled (Default mode). Drives banner visibility.</summary>
    [ObservableProperty] private bool _isDefaultMode;

    // ── Identity badge (driven by CurrentUserHolder.UserChanged via OnCapabilitiesChanged) ──
    [ObservableProperty] private string _userRole = "Default";
    [ObservableProperty] private string _userDisplayName = "Default user";

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
        _settingsWindow.Owner = FindOwnerWindow();
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
        _userManagementWindow.Owner = FindOwnerWindow();
        _userManagementWindow.ShowDialog();
    }

    /// <summary>Called during initialization and on mode changes to refresh IsDefaultMode.</summary>
    private void RefreshDefaultModeState()
    {
        var systemMode = App.Services.GetService<Services.SystemModeClient>();
        IsDefaultMode = systemMode is null || !systemMode.IsSecuredMode;
    }

    /// <summary>Updates UserRole / UserDisplayName from CurrentUserHolder.</summary>
    private void RefreshUserBadge()
    {
        var user = _currentUserHolder.User;
        UserRole = user.Roles.FirstOrDefault() ?? "Default";
        UserDisplayName = user.DisplayName ?? "Default user";
    }

    /// <summary>Direct handler for CurrentUserHolder.UserChanged — ensures badge always reflects login/logout.</summary>
    private void OnCurrentUserChanged()
    {
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            if (dispatcher.CheckAccess())
                RefreshUserBadge();
            else
                dispatcher.InvokeAsync(RefreshUserBadge);
        }
    }

    /// <summary>
    /// Re-subscribes to events and refreshes UI state.
    /// Called when a new MainWindow reuses this Singleton after a prior Dispose.
    /// </summary>
    public void EnsureSubscriptions()
    {
        // Idempotent: unsubscribe first to avoid double-subscribe
        _capabilityChecker.CapabilitiesChanged -= OnCapabilitiesChanged;
        _authClient.AuthStateChanged -= OnAuthStateChanged;
        _lockStateService.LocksChanged -= OnLocksChanged;
        _currentUserHolder.UserChanged -= OnCurrentUserChanged;

        _capabilityChecker.CapabilitiesChanged += OnCapabilitiesChanged;
        _authClient.AuthStateChanged += OnAuthStateChanged;
        _lockStateService.LocksChanged += OnLocksChanged;
        _currentUserHolder.UserChanged += OnCurrentUserChanged;

        // Refresh badge and mode state from current holder values
        RefreshDefaultModeState();
        RefreshUserBadge();
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        await _authClient.LogoutAsync();
        // CurrentUserHolder.Clear() fires via OnAuthStateChanged → badge reverts to Default.
        // Navigate to LoginPage.
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var loginPage = new LoginPage();
            loginPage.Show();
            FindOwnerWindow()?.Close();
        });
    }

    /// <summary>Finds the MainWindow instance to use as dialog Owner (avoids Owner=itself crash).</summary>
    private static Window? FindOwnerWindow()
    {
        // Prefer the actual MainWindow type; fall back to the active window.
        if (Application.Current is null) return null;
        foreach (Window w in Application.Current.Windows)
        {
            if (w is MainWindow) return w;
        }
        return Application.Current.MainWindow;
    }
}
