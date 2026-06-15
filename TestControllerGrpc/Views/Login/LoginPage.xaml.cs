using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels;
using TestControllerGrpc.ViewModels.Login;

namespace TestControllerGrpc.Views.Login;

public partial class LoginPage : Window
{
    private readonly LoginViewModel _viewModel;
    private bool _isPasswordVisible;

    public LoginPage()
    {
        InitializeComponent();
        _viewModel = App.Services.GetRequiredService<LoginViewModel>();
        DataContext = _viewModel;

        // Wire PasswordBox (can't bind directly due to WPF security)
        PasswordBox.PasswordChanged += (_, _) =>
        {
            if (!_isPasswordVisible)
                _viewModel.Password = PasswordBox.Password;
        };

        _viewModel.LoginSucceeded += OnLoginSucceeded;

        // If mode switches to Default while on LoginPage, login is no longer required
        var systemMode = App.Services.GetService<SystemModeClient>();
        if (systemMode is not null)
        {
            systemMode.ModeChanged += OnSystemModeChanged;
            Closed += (_, _) => systemMode.ModeChanged -= OnSystemModeChanged;
        }

        Loaded += (_, _) => UsernameBox.Focus();
    }

    private void OnSystemModeChanged(string mode)
    {
        if (!string.Equals(mode, "default", StringComparison.OrdinalIgnoreCase))
            return;

        if (Dispatcher.CheckAccess())
        {
            HandleSwitchToDefault();
        }
        else
        {
            Dispatcher.Invoke(HandleSwitchToDefault);
        }
    }

    private void HandleSwitchToDefault()
    {
        // Default mode: no login required — show MainWindow directly
        var holder = App.Services.GetRequiredService<CurrentUserHolder>();
        holder.SetMode(false);
        holder.SetDefaultUser();

        ShowOrCreateMainWindow();
        Close();
    }

    private void TogglePasswordVisibility_Click(object sender, RoutedEventArgs e)
    {
        _isPasswordVisible = !_isPasswordVisible;

        if (_isPasswordVisible)
        {
            PasswordTextBox.Text = PasswordBox.Password;
            PasswordBox.Visibility = Visibility.Collapsed;
            PasswordTextBox.Visibility = Visibility.Visible;
            TogglePasswordBtn.Content = "🙈";
        }
        else
        {
            PasswordBox.Password = PasswordTextBox.Text;
            PasswordTextBox.Visibility = Visibility.Collapsed;
            PasswordBox.Visibility = Visibility.Visible;
            TogglePasswordBtn.Content = "👁";
        }
    }

    private void OnLoginSucceeded(bool mustChangePassword)
    {
        _viewModel.LoginSucceeded -= OnLoginSucceeded;

        // Propagate authenticated identity to CurrentUserHolder before MainWindow renders.
        // MainViewModel subscribes to AuthStateChanged on construction, but the event
        // already fired during LoginAsync → FetchMeAsync, so we seed it here.
        var authClient = App.Services.GetRequiredService<AuthClient>();
        var holder = App.Services.GetRequiredService<CurrentUserHolder>();

        // FIX 3: WPF is Administrator-only (SRS §2.2).
        // Reject non-admin users — they must use the Web Client.
        if (authClient.CurrentUser is not null
            && !string.Equals(authClient.CurrentUser.Role, "Administrator", StringComparison.OrdinalIgnoreCase))
        {
            _viewModel.ErrorMessage = "This application requires Administrator access. Please use the Web Client.";
            _ = authClient.LogoutAsync(); // revoke session server-side (fire-and-forget)
            _viewModel.LoginSucceeded += OnLoginSucceeded; // re-subscribe for retry
            return;
        }

        if (authClient.CurrentUser is not null)
            holder.SetUser(authClient.CurrentUser);

        if (mustChangePassword)
        {
            var changePasswordPage = new ChangePasswordPage();
            changePasswordPage.PasswordChangeCompleted += OnPasswordChangeCompleted;
            changePasswordPage.Show();
        }
        else
        {
            ShowOrCreateMainWindow();
        }

        Close();
    }

    private void OnPasswordChangeCompleted()
    {
        // After forced password change, user must log in again with new credentials
        var loginPage = new LoginPage();
        loginPage.Show();
    }

    /// <summary>
    /// Finds an existing hidden MainWindow and shows it, or creates a new one
    /// (handles the case where the app started directly in Secured mode).
    /// </summary>
    private static void ShowOrCreateMainWindow()
    {
        foreach (Window w in Application.Current.Windows)
        {
            if (w is MainWindow existing)
            {
                existing.Show();
                existing.ShowInTaskbar = true;
                existing.WindowState = WindowState.Normal;
                existing.Activate();
                // Re-seed badge, mode, and tab state after mode switch while hidden
                var vm = existing.DataContext as MainViewModel;
                vm?.EnsureSubscriptions();
                return;
            }
        }

        // No existing MainWindow (initial Secured-mode startup) — create one
        var mainWindow = new MainWindow();
        mainWindow.Show();
    }
}
