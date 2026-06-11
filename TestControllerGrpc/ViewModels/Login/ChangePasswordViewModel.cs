using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels.Login;

/// <summary>
/// ViewModel for the first-login forced password change screen.
/// Validates: min 12 chars, mixed case + digit, passwords match.
/// Requires current password verification even on forced change.
/// </summary>
public sealed partial class ChangePasswordViewModel : ObservableObject
{
    private readonly AuthClient _authClient;
    private readonly IAppLogger _logger;

    [ObservableProperty] private string _currentPassword = "";
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private string _confirmPassword = "";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private bool _isLoading;

    /// <summary>Raised when password change succeeds.</summary>
    public event Action? PasswordChanged;

    public ChangePasswordViewModel(AuthClient authClient, IAppLogger logger)
    {
        _authClient = authClient;
        _logger = logger;
    }

    partial void OnCurrentPasswordChanged(string value) => ErrorMessage = "";
    partial void OnNewPasswordChanged(string value) => ErrorMessage = "";
    partial void OnConfirmPasswordChanged(string value) => ErrorMessage = "";

    private bool CanChangePassword() =>
        !string.IsNullOrWhiteSpace(CurrentPassword)
        && !string.IsNullOrWhiteSpace(NewPassword)
        && !string.IsNullOrWhiteSpace(ConfirmPassword)
        && !IsLoading;

    [RelayCommand(CanExecute = nameof(CanChangePassword))]
    private async Task ChangePasswordAsync()
    {
        // Client-side validation
        if (NewPassword.Length < 12)
        {
            ErrorMessage = "Password must be at least 12 characters";
            return;
        }

        if (!NewPassword.Any(char.IsLower))
        {
            ErrorMessage = "Password must contain a lowercase letter";
            return;
        }

        if (!NewPassword.Any(char.IsUpper))
        {
            ErrorMessage = "Password must contain an uppercase letter";
            return;
        }

        if (!NewPassword.Any(char.IsDigit))
        {
            ErrorMessage = "Password must contain a digit";
            return;
        }

        if (NewPassword != ConfirmPassword)
        {
            ErrorMessage = "Passwords do not match";
            return;
        }

        IsLoading = true;
        ErrorMessage = "";

        try
        {
            var (success, error) = await _authClient.ChangePasswordAsync(CurrentPassword, NewPassword);

            if (success)
            {
                _logger.Info("Auth", "WPF forced password change succeeded");
                PasswordChanged?.Invoke();
            }
            else
            {
                ErrorMessage = error ?? "Password change failed";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = "Connection error. Please try again.";
            _logger.Error("Auth", "WPF ChangePassword exception", ex);
        }
        finally
        {
            IsLoading = false;
            ChangePasswordCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnIsLoadingChanged(bool value) => ChangePasswordCommand.NotifyCanExecuteChanged();
}
