using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels.Login;

/// <summary>
/// ViewModel for the WPF login page. CommunityToolkit.Mvvm source-generated.
/// Per Mockup 1: username, password, Sign In. No guest path on WPF.
/// </summary>
public sealed partial class LoginViewModel : ObservableObject
{
    private readonly AuthClient _authClient;
    private readonly IAppLogger _logger;

    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private bool _isLoading;

    /// <summary>Raised when login succeeds. Bool param = mustChangePassword.</summary>
    public event Action<bool>? LoginSucceeded;

    public LoginViewModel(AuthClient authClient, IAppLogger logger)
    {
        _authClient = authClient;
        _logger = logger;
    }

    partial void OnUsernameChanged(string value) => ErrorMessage = "";
    partial void OnPasswordChanged(string value) => ErrorMessage = "";

    private bool CanSignIn() => !string.IsNullOrWhiteSpace(Username)
                                && !string.IsNullOrWhiteSpace(Password)
                                && !IsLoading;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        IsLoading = true;
        ErrorMessage = "";

        try
        {
            var result = await _authClient.LoginAsync(Username.Trim(), Password);

            if (result.Success)
            {
                _logger.Info("Auth", $"WPF login succeeded for {Username.Trim()}");
                LoginSucceeded?.Invoke(result.MustChangePassword);
            }
            else
            {
                ErrorMessage = result.Error ?? "Login failed";
                _logger.Warn("Auth", $"WPF login failed for {Username.Trim()}: {result.Error}");
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = "Connection error. Please try again.";
            _logger.Error("Auth", "WPF login exception", ex);
        }
        finally
        {
            IsLoading = false;
            SignInCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnIsLoadingChanged(bool value) => SignInCommand.NotifyCanExecuteChanged();
}
