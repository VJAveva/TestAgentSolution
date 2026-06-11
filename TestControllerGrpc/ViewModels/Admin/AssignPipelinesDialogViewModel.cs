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
    [ObservableProperty] private ObservableCollection<PipelineCheckItem> _pipelines = [];
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _errorMessage = "";

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
    public async Task InitializeAsync(string userId, string username, IReadOnlyList<string> allPipelineIds)
    {
        UserId = userId;
        Username = username;
        IsLoading = true;

        try
        {
            var currentAssignments = await _client.GetAssignmentsAsync(userId);
            var currentSet = new HashSet<string>(currentAssignments);

            var items = allPipelineIds
                .Select(id => new PipelineCheckItem(id, currentSet.Contains(id)))
                .ToList();

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
            var desiredIds = Pipelines
                .Where(p => p.IsChecked)
                .Select(p => p.PipelineId)
                .ToList();

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

    [ObservableProperty] private bool _isChecked;

    public PipelineCheckItem(string pipelineId, bool isChecked)
    {
        PipelineId = pipelineId;
        _isChecked = isChecked;
    }
}
