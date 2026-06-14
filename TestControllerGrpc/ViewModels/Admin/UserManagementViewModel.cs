using System.Collections.ObjectModel;
using System.Linq;
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
    private List<UserListItemViewModel> _allUsers = [];

    [ObservableProperty] private ObservableCollection<UserListItemViewModel> _users = [];
    [ObservableProperty] private UserListItemViewModel? _selectedUser;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _roleFilter = "All";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _errorMessage = "";

    /// <summary>Raised to request opening the Add User dialog.</summary>
    public event Action? RequestOpenAddUser;

    /// <summary>Raised to request opening the Assign Pipelines dialog for SelectedUser.</summary>
    public event Action? RequestOpenAssignPipelines;

    /// <summary>Raised to request opening the Reset Password dialog for SelectedUser.</summary>
    public event Action? RequestOpenResetPassword;

    /// <summary>Raised to request opening the Delete User confirmation for SelectedUser.</summary>
    public event Action? RequestOpenDeleteUser;

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

            _allUsers = items;
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
        var filtered = RoleFilter switch
        {
            "Admin" => _allUsers.Where(u => u.Role == "Administrator"),
            "Sr.Mgr" => _allUsers.Where(u => u.Role == "SeniorManager"),
            "Engineer" => _allUsers.Where(u => u.Role == "Engineer"),
            _ => _allUsers // "All" or anything else
        };
        Users = new ObservableCollection<UserListItemViewModel>(filtered);
    }

    [RelayCommand]
    private void OpenAddUser() => RequestOpenAddUser?.Invoke();

    [RelayCommand]
    private void OpenAssignPipelines()
    {
        if (SelectedUser is null) return;
        RequestOpenAssignPipelines?.Invoke();
    }

    [RelayCommand]
    private void OpenResetPassword()
    {
        if (SelectedUser is null) return;
        RequestOpenResetPassword?.Invoke();
    }

    [RelayCommand]
    private void OpenDeleteUser()
    {
        if (SelectedUser is null) return;
        RequestOpenDeleteUser?.Invoke();
    }
}

/// <summary>Lightweight item for the user list.</summary>
public sealed record UserListItemViewModel(
    string UserId, string Username, string Email, string Role,
    bool IsActive, bool MustChangePassword, DateTime CreatedUtc, int PipelineCount);
