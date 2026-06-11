using CommunityToolkit.Mvvm.ComponentModel;

namespace TestControllerGrpc.ViewModels.Admin;

/// <summary>
/// Dialog ViewModel for confirming user deletion.
/// Shows username, pipeline assignment count, and audit retention warning.
/// </summary>
public sealed partial class DeleteUserConfirmDialogViewModel : ObservableObject
{
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private int _assignmentCount;
    [ObservableProperty] private bool _canConfirm;

    public DeleteUserConfirmDialogViewModel(string username, int assignmentCount)
    {
        _username = username;
        _assignmentCount = assignmentCount;

        // Delay enabling confirm button for 2 seconds to prevent accidental clicks
        _ = EnableConfirmAfterDelayAsync();
    }

    private async Task EnableConfirmAfterDelayAsync()
    {
        await Task.Delay(2000);
        CanConfirm = true;
    }

    public string WarningText => AssignmentCount > 0
        ? $"{Username} has {AssignmentCount} pipeline assignment(s) that will be removed."
        : $"{Username} has no pipeline assignments.";

    public string AuditRetentionText => "Audit history for this user will be retained.";
}
