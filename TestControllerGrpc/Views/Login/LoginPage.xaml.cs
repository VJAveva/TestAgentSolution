using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.ViewModels.Login;

namespace TestControllerGrpc.Views.Login;

public partial class LoginPage : Window
{
    private readonly LoginViewModel _viewModel;

    public LoginPage()
    {
        InitializeComponent();
        _viewModel = App.Services.GetRequiredService<LoginViewModel>();
        DataContext = _viewModel;

        // Wire PasswordBox (can't bind directly due to WPF security)
        PasswordBox.PasswordChanged += (_, _) => _viewModel.Password = PasswordBox.Password;

        _viewModel.LoginSucceeded += OnLoginSucceeded;

        Loaded += (_, _) => UsernameBox.Focus();
    }

    private void OnLoginSucceeded(bool mustChangePassword)
    {
        _viewModel.LoginSucceeded -= OnLoginSucceeded;

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
