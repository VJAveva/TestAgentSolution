using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using System.Windows;
using TestAgentGrpc;
using TestControllerGrpc.Helpers;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.ViewModels;

// ?? Agent Registration, Test, Diagnose, Health Check, Self-registration ??
public sealed partial class MainViewModel
{
    [ObservableProperty] private AgentInfoViewModel? _selectedAgent;

    // GAP 5 fix: O(1) agent lookup by name instead of O(n) FirstOrDefault scans.
    // With 100 agents sending heartbeats every 15s, this eliminates ~700 linear
    // scans per minute from the UI thread.
    private readonly Dictionary<string, AgentInfoViewModel> _agentIndex = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>O(1) lookup of agent by name. Returns null if not found.</summary>
    private AgentInfoViewModel? FindAgent(string name)
        => _agentIndex.TryGetValue(name, out var vm) ? vm : null;

    /// <summary>Adds an agent to both the ObservableCollection and the index.</summary>
    private void IndexAgent(AgentInfoViewModel vm)
    {
        _agentIndex[vm.Name] = vm;
        RegisteredAgents.Add(vm);
    }

    /// <summary>Removes an agent from both the ObservableCollection and the index.</summary>
    private void UnindexAgent(AgentInfoViewModel vm)
    {
        _agentIndex.Remove(vm.Name);
        RegisteredAgents.Remove(vm);
    }

    /// <summary>
    /// Normalizes a user-supplied agent address:
    ///   "jvkbak:5200"          ? "http://jvkbak:5200"
    ///   "jvkbak"               ? "http://jvkbak:5200"
    ///   "http://jvkbak:5200"   ? "http://jvkbak:5200"  (no change)
    ///   "https://jvkbak:5200"  ? "https://jvkbak:5200" (no change)
    /// </summary>
    internal static string NormalizeAgentAddress(string raw, int defaultPort = 5200)
    {
        var address = raw?.Trim() ?? "";
        if (string.IsNullOrEmpty(address)) return address;

        if (!address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            address = "http://" + address;
        }

        if (Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            // Port -1 means no port in URI; port 80 is the http default (unlikely for gRPC)
            if (uri.Port is -1 or 80 && uri.Scheme == "http")
                address = $"{uri.Scheme}://{uri.Host}:{defaultPort}";
        }

        return address;
    }

    [RelayCommand]
    private async Task RegisterAgent()
    {
        if (string.IsNullOrWhiteSpace(NewAgentName))
        {
            AddLog("Agent name required.");
            return;
        }

        var name = NewAgentName.Trim();
        var addr = NormalizeAgentAddress(NewAgentAddress);
        NewAgentAddress = addr;

        if (!Uri.TryCreate(addr, UriKind.Absolute, out _))
        {
            AddLog($"Invalid address: {NewAgentAddress}. Use http://hostname:port");
            return;
        }

        // Remove existing if re-registering
        var existing = FindAgent(name);
        if (existing is not null) UnindexAgent(existing);

        // Register in dispatcher
        _dispatcher.RegisterAgent(name, addr);

        // Create view model and add to list
        var agentVm = new AgentInfoViewModel
        {
            Name = name, Address = addr, ConnectionStatus = "Testing"
        };
        agentVm.UpdateDetailLine();
        IndexAgent(agentVm);
        AddLog($"{LogIcons.Info} Registering agent: {name} {LogIcons.Arrow} {addr}...");
        NewAgentName = "";

        // Test connectivity
        await TestSingleAgentAsync(agentVm);
    }

    [RelayCommand]
    private async Task TestConnection()
    {
        if (SelectedAgent is null) return;
        await TestSingleAgentAsync(SelectedAgent);
    }

    [RelayCommand]
    private async Task TestAllAgents()
    {
        await TestAllAgentsAsync();
    }

    [RelayCommand]
    private async Task DiagnoseAgent()
    {
        if (SelectedAgent is null) return;
        var agent = SelectedAgent;

        // Guard: don't run diagnostics (gRPC calls) during active execution
        if (_dispatcher.IsAgentExecuting(agent.Name))
        {
            AddLog($"{LogIcons.Diagnose} Diagnostics skipped for {agent.Name} — agent is currently executing", LogSeverity.Warning);
            return;
        }

        agent.IsDiagnosing = true;
        agent.ConnectionStatus = "Testing";
        agent.UpdateDetailLine();

        AddLog($"{LogIcons.Diagnose} Diagnosing {agent.Name} ({agent.Address})");

        try
        {
            var steps = await _dispatcher.DiagnoseAgentAsync(agent.Name);
            foreach (var step in steps)
            {
                var icon = LogIcons.ForDiagnosticStep(step.Passed, step.IsFatal);
                var stepSeverity = step.Passed ? LogSeverity.Success
                    : step.IsFatal ? LogSeverity.Error
                    : LogSeverity.Warning;
                AddLog($"  {icon} {step.Name}: {step.Detail}", stepSeverity);
            }

            var allPassed = steps.All(s => s.Passed || !s.IsFatal);
            var lastFailed = steps.LastOrDefault(s => !s.Passed && s.IsFatal);

            if (allPassed)
            {
                agent.ConnectionStatus = "Online";
                agent.ErrorDetail = "";
                // Also update metrics from the last step (Snapshot)
                await TestSingleAgentAsync(agent);
            }
            else
            {
                agent.ConnectionStatus = "Offline";
                agent.ErrorDetail = lastFailed?.Detail ?? "Unknown failure";
                agent.UpdateDetailLine();
            }

            var resultSeverity = allPassed ? LogSeverity.Success : LogSeverity.Error;
            var resultIcon = allPassed ? LogIcons.Success : LogIcons.Error;
            AddLog($"{resultIcon} Diagnosis complete: {(allPassed ? "ALL PASSED" : $"FAILED at {lastFailed?.Name}")}", resultSeverity);
        }
        catch (Exception ex)
        {
            agent.ConnectionStatus = "Error";
            agent.ErrorDetail = ex.Message;
            agent.UpdateDetailLine();
            AddLog($"  {LogIcons.Error} Diagnosis error: {ex.Message}", LogSeverity.Error);
        }
        finally
        {
            agent.IsDiagnosing = false;
        }
    }

    [RelayCommand]
    private void UnregisterAgent()
    {
        if (SelectedAgent is null) return;
        var name = SelectedAgent.Name;
        _dispatcher.UnregisterAgent(name);
        UnindexAgent(SelectedAgent);
        SelectedAgent = null;
        AddLog($"Unregistered agent: {name}");
        RefreshAgentStatusSummary();
    }

    [RelayCommand]
    private void OpenAgentMonitor(AgentInfoViewModel? agent)
    {
        if (agent is null) return;

        var address = _dispatcher.GetAgentAddress(agent.Name);
        if (string.IsNullOrEmpty(address))
        {
            AddLog($"Agent '{agent.Name}' has no registered address.", LogSeverity.Warning);
            return;
        }

        var vm = new AgentMonitorViewModel(agent.Name, address);
        var window = new Views.AgentMonitorWindow { DataContext = vm };

        if (FindOwnerWindow() is { } main && !ReferenceEquals(main, window))
            window.Owner = main;

        window.Closed += (_, _) => vm.Dispose();
        window.Show();
    }

    private async Task TestSingleAgentAsync(AgentInfoViewModel agentVm)
    {
        agentVm.ConnectionStatus = "Testing";
        agentVm.UpdateDetailLine();

        var (snapshot, error) = await _dispatcher.TestConnectionAsync(agentVm.Name);

        if (snapshot is not null)
        {
            var stateLabel = snapshot.State switch
            {
                AgentState.Ready => "Ready",
                AgentState.Running => "Running",
                _ => "Inactive"
            };
            agentVm.AgentState = stateLabel;

            if (snapshot.Metrics is not null)
            {
                agentVm.CpuUsage = $"{snapshot.Metrics.CpuUsagePct:F0}%";
                agentVm.MemoryUsage = $"{snapshot.Metrics.MemoryUsedMb:F0}MB";
                agentVm.DiskFree = $"{snapshot.Metrics.DiskFreeGb:F1}GB";
            }
            else
            {
                agentVm.CpuUsage = "\u2014";
                agentVm.MemoryUsage = "\u2014";
                agentVm.DiskFree = "\u2014";
            }

            agentVm.ConnectionStatus = "Online";
            agentVm.ErrorDetail = "";
            agentVm.UpdateDetailLine();
            AddLog($"{LogIcons.Success} Agent {agentVm.Name}: {stateLabel} | CPU: {agentVm.CpuUsage} | Mem: {agentVm.MemoryUsage} | Disk: {agentVm.DiskFree}", LogSeverity.Success);
        }
        else
        {
            agentVm.ConnectionStatus = "Offline";
            agentVm.AgentState = "\u2014";
            agentVm.CpuUsage = "\u2014";
            agentVm.MemoryUsage = "\u2014";
            agentVm.DiskFree = "\u2014";
            agentVm.ErrorDetail = error ?? "Unknown error";
            agentVm.UpdateDetailLine();
            AddLog($"{LogIcons.Error} Agent {agentVm.Name}: {error}", LogSeverity.Error);
        }
        RefreshAgentStatusSummary();
    }

    private async Task TestAllAgentsAsync()
    {
        AddLog($"{LogIcons.Info} Testing {RegisteredAgents.Count} agent(s)...");
        var tasks = RegisteredAgents.Select(TestSingleAgentAsync).ToArray();
        await Task.WhenAll(tasks);
        var online = RegisteredAgents.Count(a => a.ConnectionStatus == "Online");
        AddLog($"Agent check complete: {online}/{RegisteredAgents.Count} online");
        RefreshAgentStatusSummary();
    }

    private void RefreshAgentStatusSummary()
    {
        if (RegisteredAgents.Count == 0)
        {
            AgentStatusSummary = "No agents";
            return;
        }
        var online = RegisteredAgents.Count(a => a.ConnectionStatus == "Online");
        AgentStatusSummary = $"{online}/{RegisteredAgents.Count} online";
    }

    // ?? Background agent health check (GAP 2 fix) ????????????????????
    // Replaced DispatcherTimer (UI-thread sequential) with a background
    // Task that pings all agents in parallel via Task.WhenAll, then
    // marshals only the UI property updates to the Dispatcher.
    // With 100 agents � 5s timeout, worst case = 5s (parallel) vs 500s (sequential).

    private CancellationTokenSource? _healthCheckCts;

    /// <summary>Starts background health-check polling for all registered agents.</summary>
    public void StartPeriodicHealthCheck(int intervalSeconds = 30)
    {
        StopPeriodicHealthCheck();
        _healthCheckCts = new CancellationTokenSource();
        var ct = _healthCheckCts.Token;
        var interval = TimeSpan.FromSeconds(intervalSeconds);

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, ct);
                }
                catch (OperationCanceledException) { break; }

                // Snapshot the agent list on the UI thread
                List<AgentInfoViewModel> agents = [];
                try
                {
                    agents = await Application.Current!.Dispatcher.InvokeAsync(
                        () => RegisteredAgents.ToList());
                }
                catch { break; }

                if (agents.Count == 0) continue;

                // Ping ALL agents in parallel on background threads
                var tasks = agents.Select(async agent =>
                {
                    try
                    {
                        var (snapshot, _) = await _dispatcher.TestConnectionAsync(agent.Name, ct);

                        // Marshal UI updates to dispatcher
                        await Application.Current!.Dispatcher.InvokeAsync(() =>
                        {
                            if (snapshot is not null)
                            {
                                agent.AgentState = snapshot.State switch
                                {
                                    AgentState.Ready => "Ready",
                                    AgentState.Running => "Running",
                                    _ => "Inactive"
                                };
                                if (snapshot.Metrics is not null)
                                {
                                    agent.CpuUsage = $"{snapshot.Metrics.CpuUsagePct:F0}%";
                                    agent.MemoryUsage = $"{snapshot.Metrics.MemoryUsedMb:F0}MB";
                                    agent.DiskFree = $"{snapshot.Metrics.DiskFreeGb:F1}GB";
                                }
                                agent.ConnectionStatus = "Online";
                                agent.ErrorDetail = "";
                            }
                            else
                            {
                                agent.ConnectionStatus = "Offline";
                            }
                            agent.UpdateDetailLine();
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Background health check failed for {Agent}", agent.Name);
                    }
                });

                await Task.WhenAll(tasks);

                // Update summary on UI thread
                try
                {
                    await Application.Current!.Dispatcher.InvokeAsync(RefreshAgentStatusSummary);
                }
                catch { /* app shutting down */ }
            }
        }, ct);
    }

    public void StopPeriodicHealthCheck()
    {
        _healthCheckCts?.Cancel();
        _healthCheckCts?.Dispose();
        _healthCheckCts = null;
    }

    // ?? Agent self-registration via gRPC server events ??????????????

    private void OnAgentSelfRegistered(string name, string address)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            address = NormalizeAgentAddress(address);

            // If already in list, update address; else add new
            var existing = FindAgent(name);
            if (existing is not null)
            {
                existing.Address = address;
                existing.ConnectionStatus = "Online";
                existing.AgentState = "Ready";
                existing.UpdateDetailLine();
            }
            else
            {
                var vm = new AgentInfoViewModel
                {
                    Name = name, Address = address,
                    ConnectionStatus = "Online", AgentState = "Ready"
                };
                vm.UpdateDetailLine();
                IndexAgent(vm);
                AddLog($"{LogIcons.Success} Agent self-registered: {name} {LogIcons.Arrow} {address}", LogSeverity.Success);
            }
            RefreshAgentStatusSummary();
        });
    }

    private void OnAgentSelfUnregistered(string name)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var existing = FindAgent(name);
            if (existing is not null)
            {
                existing.ConnectionStatus = "Offline";
                existing.AgentState = "Shutdown";
                existing.UpdateDetailLine();
            }
            AddLog($"{LogIcons.Warning} Agent unregistered: {name}", LogSeverity.Warning);
            RefreshAgentStatusSummary();
        });
    }

    private void OnAgentHeartbeat(string name, AgentState state, ResourceMetrics? metrics)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var existing = FindAgent(name);

            if (existing is null)
            {
                // Agent is heartbeating but not in the UI list � add it if registered in dispatcher
                var address = _dispatcher.GetAgentAddress(name);
                if (address is null) return;

                existing = new AgentInfoViewModel { Name = name, Address = address };
                IndexAgent(existing);
                AddLog($"{LogIcons.Info} Agent discovered via heartbeat: {name} {LogIcons.Arrow} {address}");
            }

            existing.ConnectionStatus = "Online";
            existing.AgentState = state switch
            {
                AgentState.Ready => "Ready",
                AgentState.Running => "Running",
                _ => "Inactive"
            };
            if (metrics is not null)
            {
                existing.CpuUsage = $"{metrics.CpuUsagePct:F0}%";
                existing.MemoryUsage = $"{metrics.MemoryUsedMb:F0}MB";
                existing.DiskFree = $"{metrics.DiskFreeGb:F1}GB";
            }
            existing.LastChecked = DateTime.Now.ToString("HH:mm:ss");
            existing.UpdateDetailLine();
            RefreshAgentStatusSummary();
        });
    }
}
