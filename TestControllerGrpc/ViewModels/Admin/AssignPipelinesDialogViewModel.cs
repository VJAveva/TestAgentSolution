using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels.Admin;

/// <summary>
/// Dialog ViewModel for assigning pipelines to a user.
/// Shows all WatchItems with checkboxes; current assignments pre-checked.
/// Submits diff (desired set) to server in one transaction.
/// </summary>
public sealed partial class AssignPipelinesDialogViewModel : ObservableObject
{
    private readonly UserManagementClient _client;
    private readonly IAppLogger _logger;

    [ObservableProperty] private string _userId = "";
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _role = "";
    [ObservableProperty] private ObservableCollection<PipelineCheckItem> _pipelines = [];
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _errorMessage = "";

    public bool IsAdministrator => string.Equals(Role, "Administrator", StringComparison.OrdinalIgnoreCase);
    public bool IsSeniorManager => string.Equals(Role, "SeniorManager", StringComparison.OrdinalIgnoreCase);
    public bool IsFullAccessRole => IsAdministrator || IsSeniorManager;

    /// <summary>Raised when assignments are saved successfully.</summary>
    public event Action? AssignmentsSaved;

    public AssignPipelinesDialogViewModel(UserManagementClient client, IAppLogger logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// Initialize with user ID and available pipelines.
    /// Call after constructing, before showing the dialog.
    /// </summary>
    public async Task InitializeAsync(string userId, string username, string role, IReadOnlyList<string> allPipelineIds)
    {
        UserId = userId;
        Username = username;
        Role = role;
        IsLoading = true;

        try
        {
            List<PipelineCheckItem> items;
            if (IsFullAccessRole)
            {
                // Admins and Senior Managers have full access to all pipelines; show as pre-checked and readonly.
                items = allPipelineIds
                    .Select(id => new PipelineCheckItem(id, true, false))
                    .ToList();
            }
            else
            {
                var currentAssignments = await _client.GetAssignmentsAsync(userId);
                var currentSet = new HashSet<string>(currentAssignments);

                items = allPipelineIds
                    .Select(id => new PipelineCheckItem(id, currentSet.Contains(id), true))
                    .ToList();
            }

            Pipelines = new ObservableCollection<PipelineCheckItem>(items);
        }
        catch (Exception ex)
        {
            ErrorMessage = "Failed to load assignments";
            _logger.Error("UserMgmt", "LoadAssignments failed", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsLoading = true;
        ErrorMessage = "";

        try
        {
            var desiredIds = IsFullAccessRole
                ? Pipelines.Select(p => p.PipelineId).ToList()
                : Pipelines.Where(p => p.IsChecked).Select(p => p.PipelineId).ToList();

            var (success, error) = await _client.SetAssignmentsAsync(UserId, desiredIds);
            if (success)
            {
                _logger.Info("UserMgmt", $"Updated assignments for {Username}: {desiredIds.Count} pipelines");
                AssignmentsSaved?.Invoke();
            }
            else
            {
                ErrorMessage = error ?? "Save failed";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = "Connection error";
            _logger.Error("UserMgmt", "SaveAssignments failed", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }
}

/// <summary>Checkbox item for pipeline assignment.</summary>
public sealed partial class PipelineCheckItem : ObservableObject
{
    public string PipelineId { get; }
    public bool IsSelectable { get; }

    [ObservableProperty] private bool _isChecked;

    public PipelineCheckItem(string pipelineId, bool isChecked, bool isSelectable = true)
    {
        PipelineId = pipelineId;
        _isChecked = isChecked;
        IsSelectable = isSelectable;
    }
}
