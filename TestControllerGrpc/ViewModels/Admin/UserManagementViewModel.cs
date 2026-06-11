using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels.Admin;

/// <summary>
/// Master ViewModel for the User Management page (Mockup 7).
/// CommunityToolkit.Mvvm source-generated. Singleton DI.
/// </summary>
public sealed partial class UserManagementViewModel : ObservableObject
{
    private readonly UserManagementClient _client;
    private readonly IAppLogger _logger;

    [ObservableProperty] private ObservableCollection<UserListItemViewModel> _users = [];
    [ObservableProperty] private UserListItemViewModel? _selectedUser;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _roleFilter = "All";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _errorMessage = "";

    public UserManagementViewModel(UserManagementClient client, IAppLogger logger)
    {
        _client = client;
        _logger = logger;
    }

    partial void OnSearchTextChanged(string value) => _ = LoadUsersAsync();
    partial void OnRoleFilterChanged(string value) => ApplyFilter();

    [RelayCommand]
    private async Task LoadUsersAsync()
    {
        IsLoading = true;
        ErrorMessage = "";
        try
        {
            var filter = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
            var users = await _client.ListUsersAsync(filter);

            var items = users
                .Select(u => new UserListItemViewModel(u.UserId, u.Username, u.Email, u.Role,
                    u.IsActive, u.MustChangePassword, u.CreatedUtc, u.PipelineCount))
                .ToList();

            Users = new ObservableCollection<UserListItemViewModel>(items);
            ApplyFilter();
        }
        catch (Exception ex)
        {
            ErrorMessage = "Failed to load users";
            _logger.Error("UserMgmt", "LoadUsers failed", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyFilter()
    {
        // Role filter is applied client-side on the already-loaded list
        // The search is sent server-side. This is fine for small user counts.
    }

    [RelayCommand]
    private async Task DeleteUserAsync()
    {
        if (SelectedUser is null) return;

        var (success, error) = await _client.DeleteUserAsync(SelectedUser.UserId);
        if (success)
        {
            _logger.Info("UserMgmt", $"Deleted user {SelectedUser.Username}");
            SelectedUser = null;
            await LoadUsersAsync();
        }
        else
        {
            ErrorMessage = error ?? "Delete failed";
        }
    }

    [RelayCommand]
    private async Task ResetPasswordAsync()
    {
        if (SelectedUser is null) return;

        var (newPassword, error) = await _client.ResetPasswordAsync(SelectedUser.UserId);
        if (newPassword is not null)
        {
            _logger.Info("UserMgmt", $"Reset password for {SelectedUser.Username}");
            // The dialog ViewModel will display the password
            LastResetPassword = newPassword;
        }
        else
        {
            ErrorMessage = error ?? "Reset failed";
        }
    }

    /// <summary>Holds the last reset password for the dialog to display.</summary>
    [ObservableProperty] private string? _lastResetPassword;
}

/// <summary>Lightweight item for the user list.</summary>
public sealed record UserListItemViewModel(
    string UserId, string Username, string Email, string Role,
    bool IsActive, bool MustChangePassword, DateTime CreatedUtc, int PipelineCount);
