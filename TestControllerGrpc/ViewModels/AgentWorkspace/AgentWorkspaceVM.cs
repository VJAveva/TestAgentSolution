using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Core.Maintenance;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels.AgentWorkspace;

public enum AgentWorkspaceMode { Fleet, Monitor, Registry, Maintenance }

public partial class AgentWorkspaceVM : ObservableObject, IDisposable
{
    [ObservableProperty] private AgentWorkspaceMode _currentMode = AgentWorkspaceMode.Fleet;

    public FleetVM Fleet { get; }
    public MonitorVM Monitor { get; }
    public RegistryVM Registry { get; }
    public MaintenanceVM Maintenance { get; }

    /// <summary>Windows Update banners, notifications and rollup — shared by the Fleet and Maintenance tabs.</summary>
    public FleetUpdatesVM Updates { get; }

    public AgentWorkspaceVM(
        IAgentGrpcDispatcher dispatcher,
        AgentLockManager lockManager,
        ExecutionSessionManager sessionManager,
        IEventAggregator events,
        Dispatcher uiDispatcher,
        IFleetMaintenanceService? maintenanceService = null,
        IMaintenanceStateStore? maintenanceState = null,
        IMaintenanceOperationStore? maintenanceStore = null,
        INodeUpdateStatusStore? updateStatus = null,
        IFleetNotificationService? notifications = null,
        UpdatePolicyStore? updatePolicy = null)
    {
        Fleet = new FleetVM(dispatcher, lockManager, sessionManager, events, uiDispatcher, maintenanceService, maintenanceState);
        Monitor = new MonitorVM(dispatcher, lockManager, sessionManager, events, uiDispatcher);
        Registry = new RegistryVM(dispatcher, lockManager, events, uiDispatcher);
        Maintenance = new MaintenanceVM(maintenanceService, maintenanceStore, uiDispatcher);
        Updates = new FleetUpdatesVM(dispatcher, uiDispatcher, updateStatus, notifications, maintenanceService, updatePolicy);

        Fleet.AttachUpdates(Updates);
        Maintenance.AttachUpdates(Updates);

        Fleet.AgentSelected += OnFleetAgentSelected;
        Fleet.RegisterAgentClicked += () => RegisterNew();
        Monitor.BackRequested += () => CurrentMode = AgentWorkspaceMode.Fleet;
        Updates.ReviewRequested += () => CurrentMode = AgentWorkspaceMode.Maintenance;
    }

    private void OnFleetAgentSelected(string agentName)
    {
        Monitor.LoadAgent(agentName);
        CurrentMode = AgentWorkspaceMode.Monitor;
    }

    [RelayCommand]
    private void SwitchMode(string? modeName)
    {
        if (Enum.TryParse<AgentWorkspaceMode>(modeName, out var mode))
            CurrentMode = mode;
    }

    [RelayCommand]
    private void RegisterNew()
    {
        CurrentMode = AgentWorkspaceMode.Registry;
        Registry.StartAddNew();
    }

    public void Dispose()
    {
        Fleet.Dispose();
        Registry.Dispose();
        Maintenance.Dispose();
        Updates.Dispose();
    }
}
