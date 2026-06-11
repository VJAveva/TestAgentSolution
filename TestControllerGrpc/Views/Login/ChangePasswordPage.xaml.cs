using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.ViewModels.Login;

namespace TestControllerGrpc.Views.Login;

public partial class ChangePasswordPage : Window
{
    private readonly ChangePasswordViewModel _viewModel;

    /// <summary>Raised when password change completes successfully.</summary>
    public event Action? PasswordChangeCompleted;

    public ChangePasswordPage()
    {
        InitializeComponent();
        _viewModel = App.Services.GetRequiredService<ChangePasswordViewModel>();
        DataContext = _viewModel;

        // Wire PasswordBoxes (can't bind directly due to WPF security)
        CurrentPasswordBox.PasswordChanged += (_, _) => _viewModel.CurrentPassword = CurrentPasswordBox.Password;
        NewPasswordBox.PasswordChanged += (_, _) => _viewModel.NewPassword = NewPasswordBox.Password;
        ConfirmPasswordBox.PasswordChanged += (_, _) => _viewModel.ConfirmPassword = ConfirmPasswordBox.Password;

        _viewModel.PasswordChanged += OnPasswordChanged;

        Loaded += (_, _) => CurrentPasswordBox.Focus();
    }

    private void OnPasswordChanged()
    {
        _viewModel.PasswordChanged -= OnPasswordChanged;
        PasswordChangeCompleted?.Invoke();
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Prevent closing without completing the password change (forced change)
        if (_viewModel.IsLoading)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }
}
