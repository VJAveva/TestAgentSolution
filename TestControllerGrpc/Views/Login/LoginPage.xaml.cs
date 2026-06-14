using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.Services;
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

        Loaded += (_, _) => UsernameBox.Focus();
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
            var mainWindow = new MainWindow();
            mainWindow.Show();
        }

        Close();
    }

    private void OnPasswordChangeCompleted()
    {
        // After forced password change, user must log in again with new credentials
        var loginPage = new LoginPage();
        loginPage.Show();
    }
}
