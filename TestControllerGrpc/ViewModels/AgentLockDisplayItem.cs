using CommunityToolkit.Mvvm.ComponentModel;

namespace TestControllerGrpc.ViewModels;

/// <summary>
/// View model for displaying agent lock state in the WPF admin panel.
/// </summary>
public sealed partial class AgentLockDisplayItem : ObservableObject
{
    [ObservableProperty] private string _agentName = "";
    [ObservableProperty] private string _sessionId = "";
    [ObservableProperty] private string _watchItemTag = "";
    [ObservableProperty] private string _userId = "";
    [ObservableProperty] private string _source = "";
    [ObservableProperty] private string _duration = "";
}
