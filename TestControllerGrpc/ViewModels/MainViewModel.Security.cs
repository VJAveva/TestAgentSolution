using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.Authorization;
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

    private AuditViewerWindow? _auditViewerWindow;

    [RelayCommand]
    private void OpenAuditViewer()
    {
        if (!_capabilityChecker.Can(Permission.Audit_View)) return;

        if (_auditViewerWindow is not null && _auditViewerWindow.IsLoaded)
        {
            _auditViewerWindow.Activate();
            if (_auditViewerWindow.WindowState == WindowState.Minimized)
                _auditViewerWindow.WindowState = WindowState.Normal;
            return;
        }

        _auditViewerWindow = new AuditViewerWindow();
        _auditViewerWindow.Closed += (_, _) => _auditViewerWindow = null;
        _auditViewerWindow.Owner = FindOwnerWindow();
        _auditViewerWindow.ShowDialog();
    }

    /// <summary>Called during initialization and on mode changes to refresh IsDefaultMode.</summary>
    private void RefreshDefaultModeState()
    {
        // Use CurrentUserHolder.IsSecuredMode — it has the authoritative override
        // from SetMode() which is set immediately on ModeChanged, bypassing stale IOptionsMonitor.
        IsDefaultMode = !_currentUserHolder.IsSecuredMode;
    }

    /// <summary>Updates UserRole / UserDisplayName from CurrentUserHolder.</summary>
    private void RefreshUserBadge()
    {
        var user = _currentUserHolder.User;
        UserRole = user.Roles.FirstOrDefault() ?? "Default";
        UserDisplayName = user.DisplayName ?? "Default user";
    }

    /// <summary>Refreshes Users tab visibility: visible only in Secured mode for Admin.</summary>
    private void RefreshUsersTabVisibility()
    {
        IsUsersTabVisible = !IsDefaultMode && _capabilityChecker.Can(Permission.User_Create);
    }

    /// <summary>Direct handler for CurrentUserHolder.UserChanged — ensures badge and tab always reflect login/logout.</summary>
    private void OnCurrentUserChanged()
    {
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            if (dispatcher.CheckAccess())
            {
                RefreshDefaultModeState();
                RefreshUserBadge();
                RefreshUsersTabVisibility();
            }
            else
            {
                dispatcher.InvokeAsync(() =>
                {
                    RefreshDefaultModeState();
                    RefreshUserBadge();
                    RefreshUsersTabVisibility();
                });
            }
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
        _lockStateService.AssignmentsChanged -= OnAssignmentsChanged;
        _currentUserHolder.UserChanged -= OnCurrentUserChanged;

        _capabilityChecker.CapabilitiesChanged += OnCapabilitiesChanged;
        _authClient.AuthStateChanged += OnAuthStateChanged;
        _lockStateService.LocksChanged += OnLocksChanged;
        _lockStateService.AssignmentsChanged += OnAssignmentsChanged;
        _currentUserHolder.UserChanged += OnCurrentUserChanged;

        // Refresh badge, mode state, and tab visibility from current holder values
        RefreshDefaultModeState();
        RefreshUserBadge();
        RefreshUsersTabVisibility();
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        await _authClient.LogoutAsync();
        // CurrentUserHolder.Clear() fires via OnAuthStateChanged → badge reverts to Default.
        // Hide MainWindow (reuse after next login) and navigate to LoginPage.
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var mainWindow = FindOwnerWindow();
            if (mainWindow is not null)
            {
                mainWindow.Hide();
                mainWindow.ShowInTaskbar = false;
            }
            var loginPage = new LoginPage();
            loginPage.Show();
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
