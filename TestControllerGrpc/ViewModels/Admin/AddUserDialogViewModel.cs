using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels.Admin;

/// <summary>
/// Dialog ViewModel for adding a new user.
/// Role defaults to Engineer. Username validated inline (debounced).
/// Cannot create Administrator via UI.
/// </summary>
public sealed partial class AddUserDialogViewModel : ObservableObject
{
    private readonly UserManagementClient _client;
    private readonly IAppLogger _logger;
    private CancellationTokenSource? _usernameCheckCts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private string _username = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private string _email = "";

    [ObservableProperty] private string _selectedRole = "Engineer";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private string _usernameError = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isUsernameChecking;

    /// <summary>Available roles for creation (no Administrator).</summary>
    public string[] AvailableRoles { get; } = ["Engineer", "SeniorManager"];

    /// <summary>Raised on successful creation. Carries the generated password.</summary>
    public event Action<string>? UserCreated;

    public AddUserDialogViewModel(UserManagementClient client, IAppLogger logger)
    {
        _client = client;
        _logger = logger;
    }

    partial void OnUsernameChanged(string value)
    {
        ErrorMessage = "";
        UsernameError = "";
        _ = CheckUsernameAsync(value);
    }

    private async Task CheckUsernameAsync(string username)
    {
        _usernameCheckCts?.Cancel();
        if (string.IsNullOrWhiteSpace(username))
        {
            UsernameError = "";
            return;
        }

        _usernameCheckCts = new CancellationTokenSource();
        var token = _usernameCheckCts.Token;

        try
        {
            // Debounce 300ms
            await Task.Delay(300, token);
            IsUsernameChecking = true;
            var exists = await _client.CheckUsernameExistsAsync(username.Trim());
            if (!token.IsCancellationRequested && exists)
                UsernameError = "Username already exists";
        }
        catch (TaskCanceledException) { }
        catch (Exception) { }
        finally
        {
            if (!token.IsCancellationRequested)
                IsUsernameChecking = false;
        }
    }

    private bool CanCreate() =>
        !string.IsNullOrWhiteSpace(Username)
        && !string.IsNullOrWhiteSpace(Email)
        && string.IsNullOrEmpty(UsernameError)
        && !IsLoading;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        IsLoading = true;
        ErrorMessage = "";
        try
        {
            var (user, generatedPassword, error) = await _client.CreateUserAsync(
                Username.Trim(), Email.Trim(), SelectedRole);

            if (user is not null && generatedPassword is not null)
            {
                _logger.Info("UserMgmt", $"Created user {Username.Trim()} ({SelectedRole})");
                UserCreated?.Invoke(generatedPassword);
            }
            else
            {
                ErrorMessage = error ?? "Create failed";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = "Connection error";
            _logger.Error("UserMgmt", "CreateUser failed", ex);
        }
        finally
        {
            IsLoading = false;
            CreateCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnIsLoadingChanged(bool value) => CreateCommand.NotifyCanExecuteChanged();
    partial void OnUsernameErrorChanged(string value) => CreateCommand.NotifyCanExecuteChanged();
}
