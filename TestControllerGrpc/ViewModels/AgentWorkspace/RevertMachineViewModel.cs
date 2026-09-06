using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Core.Maintenance;

namespace TestControllerGrpc.ViewModels.AgentWorkspace;

/// <summary>
/// View model for <c>RevertMachineDialog</c> (spec R4/R9/R10). Collects the snapshot + options for a single-node
/// revert, surfaces a busy warning from <see cref="IFleetMaintenanceService.PrecheckAsync"/>, and issues the
/// revert on confirm. Progress afterwards is reflected on the fleet card, not here.
/// </summary>
public partial class RevertMachineViewModel : ObservableObject
{
    private readonly IFleetMaintenanceService _maintenance;

    public RevertMachineViewModel(IFleetMaintenanceService maintenance) => _maintenance = maintenance;

    [ObservableProperty] private string _agentName = "";
    [ObservableProperty] private string _snapshotName = "";
    [ObservableProperty] private string _reason = "";
    [ObservableProperty] private bool _waitForAgent = true;
    [ObservableProperty] private bool _runPrep = true;
    [ObservableProperty] private bool _installBuild;
    [ObservableProperty] private bool _forceIfBusy;
    [ObservableProperty] private bool _isBusyWarningVisible;
    [ObservableProperty] private string _busyWarning = "";
    [ObservableProperty] private bool _submitSucceeded;

    public bool CanConfirm => !string.IsNullOrWhiteSpace(SnapshotName) && !string.IsNullOrWhiteSpace(AgentName);
    public string ConfirmText => ForceIfBusy ? "Force Revert" : "Revert";

    partial void OnSnapshotNameChanged(string value) => OnPropertyChanged(nameof(CanConfirm));
    partial void OnAgentNameChanged(string value) => OnPropertyChanged(nameof(CanConfirm));
    partial void OnForceIfBusyChanged(bool value) => OnPropertyChanged(nameof(ConfirmText));

    // Prep and build-install both need the agent back, so they are meaningless without WaitForAgent.
    partial void OnWaitForAgentChanged(bool value)
    {
        if (!value)
        {
            RunPrep = false;
            InstallBuild = false;
        }
    }

    public void Initialize(string agentName)
    {
        AgentName = agentName;
        _ = LoadPrecheckAsync(agentName);
    }

    private async Task LoadPrecheckAsync(string agentName)
    {
        try
        {
            var precheck = await _maintenance.PrecheckAsync(new[] { agentName }, CancellationToken.None);
            var node = precheck.Nodes.FirstOrDefault(n =>
                string.Equals(n.NodeId, agentName, StringComparison.OrdinalIgnoreCase));
            if (node is { RunningWatchItem: { Length: > 0 } watchItem })
            {
                BusyWarning = $"{agentName} is running '{watchItem}'. Enable \"Force revert busy agent\" to abort it.";
                IsBusyWarningVisible = true;
            }
        }
        catch
        {
            // Precheck is advisory; the engine re-checks authoritatively at Precheck phase.
        }
    }

    [RelayCommand]
    private async Task Confirm()
    {
        if (!CanConfirm)
            return;

        var request = new RevertRequest
        {
            NodeId = AgentName,
            SnapshotName = SnapshotName.Trim(),
            WaitForAgent = WaitForAgent,
            RunPrep = RunPrep,
            InstallBuild = InstallBuild,
            ForceIfBusy = ForceIfBusy,
            TriggerSource = MaintenanceTriggerSource.FleetPanel,
            TriggeredBy = Environment.UserName,
            Reason = string.IsNullOrWhiteSpace(Reason) ? null : Reason.Trim(),
        };

        try
        {
            await _maintenance.StartRevertAsync(request, CancellationToken.None);
            SubmitSucceeded = true;
        }
        catch (MaintenanceInProgressException ex)
        {
            BusyWarning = ex.Message;
            IsBusyWarningVisible = true;
        }
    }
}
