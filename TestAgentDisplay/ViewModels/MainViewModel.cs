using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestAgentDisplay.Services;
using TestAgentGrpc;

namespace TestAgentDisplay.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AgentConnectionManager _connectionManager;

    [ObservableProperty] private string _newAgentAddress = "http://localhost:5200";
    [ObservableProperty] private AgentNodeViewModel? _selectedAgent;
    [ObservableProperty] private string _statusMessage = "No agents connected.";
    [ObservableProperty] private string _commandText = "";
    [ObservableProperty] private string _commandArgs = "";

    public ObservableCollection<AgentNodeViewModel> Agents { get; } = new();

    public MainViewModel(AgentConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
        _connectionManager.EventReceived += OnEventReceived;
        _connectionManager.ConnectionStateChanged += OnConnectionChanged;
    }

    [RelayCommand]
    private async Task ConnectAgentAsync()
    {
        var addr = NewAgentAddress.Trim();
        if (string.IsNullOrEmpty(addr)) return;
        if (Agents.Any(a => a.Address == addr)) return;

        var vm = new AgentNodeViewModel
        {
            Address = addr,
            DisplayName = new Uri(addr).Host,
        };
        Agents.Add(vm);
        SelectedAgent = vm;
        StatusMessage = $"Connecting to {addr}…";

        await _connectionManager.ConnectAsync(addr);

        var snap = await _connectionManager.GetSnapshotAsync(addr);
        if (snap is not null)
            Application.Current?.Dispatcher.Invoke(() => vm.ApplySnapshot(snap));

        var hist = await _connectionManager.GetHistoryAsync(addr);
        if (hist is not null)
            Application.Current?.Dispatcher.Invoke(() => vm.ApplyHistory(hist));
    }

    [RelayCommand]
    private void DisconnectAgent()
    {
        if (SelectedAgent is null) return;
        _connectionManager.Disconnect(SelectedAgent.Address);
        Agents.Remove(SelectedAgent);
        SelectedAgent = Agents.FirstOrDefault();
        StatusMessage = $"{Agents.Count} agent(s) connected.";
    }

    [RelayCommand]
    private async Task RunCommandAsync()
    {
        if (SelectedAgent is null || string.IsNullOrWhiteSpace(CommandText)) return;
        StatusMessage = $"Sending command to {SelectedAgent.DisplayName}…";
        var reply = await _connectionManager.RunCommandAsync(
            SelectedAgent.Address, CommandText.Trim(), CommandArgs.Trim());
        StatusMessage = reply?.Accepted == true
            ? $"Command accepted (ID: {reply.ExecutionId})"
            : $"Command rejected: {reply?.Message ?? "agent unreachable"}";
    }

    [RelayCommand]
    private async Task TerminateExecutionAsync()
    {
        if (SelectedAgent is null) return;
        await _connectionManager.TerminateAsync(SelectedAgent.Address);
        StatusMessage = "Terminate signal sent.";
    }

    [RelayCommand]
    private async Task RefreshHistoryAsync()
    {
        if (SelectedAgent is null) return;
        var hist = await _connectionManager.GetHistoryAsync(SelectedAgent.Address);
        if (hist is not null)
            Application.Current?.Dispatcher.Invoke(() => SelectedAgent.ApplyHistory(hist));
    }

    private void OnEventReceived(string address, ExecutionEvent evt)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var agent = Agents.FirstOrDefault(a => a.Address == address);
            agent?.HandleEvent(evt);
        });
    }

    private void OnConnectionChanged(string address, bool connected)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var agent = Agents.FirstOrDefault(a => a.Address == address);
            agent?.SetConnected(connected);
            StatusMessage = connected
                ? $"Connected to {address}"
                : $"Disconnected from {address} — retrying…";
        });
    }

    public void Dispose()
    {
        _connectionManager.EventReceived -= OnEventReceived;
        _connectionManager.ConnectionStateChanged -= OnConnectionChanged;
    }
}
