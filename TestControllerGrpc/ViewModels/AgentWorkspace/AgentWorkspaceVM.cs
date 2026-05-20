using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels.AgentWorkspace;

public enum AgentWorkspaceMode { Fleet, Monitor, Registry }

public partial class AgentWorkspaceVM : ObservableObject
{
    [ObservableProperty] private AgentWorkspaceMode _currentMode = AgentWorkspaceMode.Fleet;

    public FleetVM Fleet { get; }
    public MonitorVM Monitor { get; }
    public RegistryVM Registry { get; }

    public AgentWorkspaceVM(
        IAgentGrpcDispatcher dispatcher,
        AgentLockManager lockManager,
        ExecutionSessionManager sessionManager,
        IEventAggregator events,
        Dispatcher uiDispatcher)
    {
        Fleet = new FleetVM(dispatcher, lockManager, sessionManager, events, uiDispatcher);
        Monitor = new MonitorVM(dispatcher, lockManager, sessionManager, events, uiDispatcher);
        Registry = new RegistryVM(dispatcher, lockManager, events, uiDispatcher);

        Fleet.AgentSelected += OnFleetAgentSelected;
        Fleet.RegisterAgentClicked += () => RegisterNew();
        Monitor.BackRequested += () => CurrentMode = AgentWorkspaceMode.Fleet;
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
}
