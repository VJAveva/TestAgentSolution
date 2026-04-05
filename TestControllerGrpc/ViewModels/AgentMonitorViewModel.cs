using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using Grpc.Net.Client;
using Google.Protobuf.WellKnownTypes;
using System.Windows;

namespace TestControllerGrpc.ViewModels;

/// <summary>
/// ViewModel for the Agent Monitor window — connects to a single agent's
/// <c>SubscribeAgentEvents</c> gRPC stream and displays live execution activity.
/// </summary>
public sealed partial class AgentMonitorViewModel : ObservableObject, IDisposable
{
    private CancellationTokenSource? _cts;
    private GrpcChannel? _channel;

    [ObservableProperty] private string _windowTitle = "";
    [ObservableProperty] private string _monitorAgentName = "";
    [ObservableProperty] private string _monitorAgentState = "Connecting\u2026";
    [ObservableProperty] private string _connectionStatusText = "Connecting";
    [ObservableProperty] private string _liveActionName = "";

    public ObservableCollection<AgentActionRow> ActionHistory { get; } = new();

    public AgentMonitorViewModel(string agentName, string agentAddress)
    {
        MonitorAgentName = agentName;
        WindowTitle = $"Agent Monitor \u2014 {agentName}";

        _ = ConnectAndStreamAsync(agentAddress);
    }

    private async Task ConnectAndStreamAsync(string address)
    {
        _cts = new CancellationTokenSource();
        try
        {
            _channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler
                {
                    EnableMultipleHttp2Connections = true,
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                    KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                    KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
                    KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
                },
                DisposeHttpClient = true,
            });

            var client = new TestAgentGrpc.TestAgentService.TestAgentServiceClient(_channel);

            // Get initial snapshot
            try
            {
                using var snapshotCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                snapshotCts.CancelAfter(TimeSpan.FromSeconds(5));
                var snapshot = await client.GetAgentSnapshotAsync(new Empty(), cancellationToken: snapshotCts.Token);
                UpdateOnUiThread(() =>
                {
                    MonitorAgentState = snapshot.State switch
                    {
                        TestAgentGrpc.AgentState.Ready => "Ready",
                        TestAgentGrpc.AgentState.Running => "Running",
                        _ => "Inactive"
                    };
                    ConnectionStatusText = "Connected";
                    if (!string.IsNullOrWhiteSpace(snapshot.CurrentCommand))
                        LiveActionName = snapshot.CurrentCommand;
                });
            }
            catch
            {
                // Snapshot not available — proceed to streaming
            }

            // Load recent execution history so the monitor shows past activities
            try
            {
                using var historyCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                historyCts.CancelAfter(TimeSpan.FromSeconds(5));
                var historyReply = await client.GetExecutionHistoryAsync(
                    new TestAgentGrpc.ExecutionHistoryRequest { MaxResults = 50 },
                    cancellationToken: historyCts.Token);
                UpdateOnUiThread(() =>
                {
                    foreach (var record in historyReply.Records)
                    {
                        var row = new AgentActionRow
                        {
                            StartTime = record.Started?.ToDateTime().ToLocalTime() ?? DateTime.MinValue,
                            Command = $"{record.Command} {record.Arguments}".Trim(),
                            ExitCode = record.ExitCode,
                            Duration = (record.Finished is not null && record.Started is not null)
                                ? record.Finished.ToDateTime() - record.Started.ToDateTime()
                                : TimeSpan.Zero,
                            StatusText = record.Outcome switch
                            {
                                TestAgentGrpc.ExecutionOutcome.OutcomeSuccess => "Success",
                                TestAgentGrpc.ExecutionOutcome.OutcomeFailed => "Failed",
                                TestAgentGrpc.ExecutionOutcome.OutcomeTerminated => "Killed",
                                TestAgentGrpc.ExecutionOutcome.OutcomeTimedOut => "Timeout",
                                _ => "Unknown"
                            },
                            StatusColor = record.Outcome switch
                            {
                                TestAgentGrpc.ExecutionOutcome.OutcomeSuccess => "#A6E3A1",
                                TestAgentGrpc.ExecutionOutcome.OutcomeFailed => "#F38BA8",
                                TestAgentGrpc.ExecutionOutcome.OutcomeTerminated => "#F38BA8",
                                TestAgentGrpc.ExecutionOutcome.OutcomeTimedOut => "#F38BA8",
                                _ => "#9399B2"
                            },
                        };
                        ActionHistory.Add(row);
                    }
                });
            }
            catch
            {
                // History not available — proceed with live streaming only
            }

            // Stream reconnect loop
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    using var call = client.SubscribeAgentEvents(new Empty(), cancellationToken: _cts.Token);
                    UpdateOnUiThread(() => ConnectionStatusText = "Connected");

                    await foreach (var evt in call.ResponseStream.ReadAllAsync(_cts.Token))
                    {
                        UpdateOnUiThread(() => ProcessEvent(evt));
                    }
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
                {
                    UpdateOnUiThread(() =>
                    {
                        ConnectionStatusText = "Reconnecting";
                        MonitorAgentState = "Offline";
                    });
                    try { await Task.Delay(5000, _cts.Token); } catch { break; }
                }
                catch (OperationCanceledException) { break; }
                catch
                {
                    UpdateOnUiThread(() =>
                    {
                        ConnectionStatusText = "Reconnecting";
                    });
                    try { await Task.Delay(3000, _cts.Token); } catch { break; }
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            UpdateOnUiThread(() =>
            {
                MonitorAgentState = "Error";
                ConnectionStatusText = $"Error: {ex.Message}";
            });
        }
    }

    private AgentActionRow? _currentAction;

    private void ProcessEvent(TestAgentGrpc.ExecutionEvent evt)
    {
        switch (evt.EventType)
        {
            case TestAgentGrpc.ExecutionEventType.EventQueued:
                _currentAction = new AgentActionRow
                {
                    StartTime = DateTime.Now,
                    Command = evt.Detail ?? evt.Command ?? "(unknown)",
                    StatusText = "Queued",
                    StatusColor = "#9399B2",
                };
                ActionHistory.Insert(0, _currentAction);
                break;

            case TestAgentGrpc.ExecutionEventType.EventStarted:
                var cmd = $"{evt.Command} {evt.Arguments}".Trim();
                if (_currentAction is not null && _currentAction.StatusText == "Queued")
                {
                    _currentAction.Command = cmd;
                    _currentAction.StatusText = "Running";
                    _currentAction.StatusColor = "#F9E2AF";
                }
                else
                {
                    _currentAction = new AgentActionRow
                    {
                        StartTime = DateTime.Now,
                        Command = cmd,
                        StatusText = "Running",
                        StatusColor = "#F9E2AF",
                    };
                    ActionHistory.Insert(0, _currentAction);
                }
                LiveActionName = cmd;
                MonitorAgentState = "Running";
                break;

            case TestAgentGrpc.ExecutionEventType.EventCompleted:
                if (_currentAction is not null)
                {
                    _currentAction.ExitCode = evt.ExitCode;
                    _currentAction.Duration = DateTime.Now - _currentAction.StartTime;
                    _currentAction.StatusText = evt.ExitCode == 0 ? "Success" : "Failed";
                    _currentAction.StatusColor = evt.ExitCode == 0 ? "#A6E3A1" : "#F38BA8";
                }
                LiveActionName = "";
                MonitorAgentState = "Ready";
                _currentAction = null;
                break;

            case TestAgentGrpc.ExecutionEventType.EventFailed:
                if (_currentAction is not null)
                {
                    _currentAction.StatusText = "Failed";
                    _currentAction.StatusColor = "#F38BA8";
                    _currentAction.Duration = DateTime.Now - _currentAction.StartTime;
                    _currentAction.ExitCode = -1;
                }
                LiveActionName = "";
                MonitorAgentState = "Ready";
                _currentAction = null;
                break;

            case TestAgentGrpc.ExecutionEventType.EventTerminated:
                if (_currentAction is not null)
                {
                    _currentAction.StatusText = "Killed";
                    _currentAction.StatusColor = "#F38BA8";
                    _currentAction.Duration = DateTime.Now - _currentAction.StartTime;
                    _currentAction.ExitCode = -1;
                }
                LiveActionName = "";
                MonitorAgentState = "Ready";
                _currentAction = null;
                break;

            case TestAgentGrpc.ExecutionEventType.EventProgress:
                if (_currentAction is not null)
                    LiveActionName = $"{evt.ProgressPct:F0}% \u2014 {evt.Detail}";
                break;

            case TestAgentGrpc.ExecutionEventType.EventStateChanged:
                MonitorAgentState = evt.AgentState switch
                {
                    TestAgentGrpc.AgentState.Ready => "Ready",
                    TestAgentGrpc.AgentState.Running => "Running",
                    _ => "Inactive"
                };
                break;

            case TestAgentGrpc.ExecutionEventType.EventHeartbeat:
                MonitorAgentState = evt.AgentState switch
                {
                    TestAgentGrpc.AgentState.Ready => "Ready",
                    TestAgentGrpc.AgentState.Running => "Running",
                    _ => "Inactive"
                };
                break;
        }
    }

    [RelayCommand]
    private void ClearHistory() => ActionHistory.Clear();

    private static void UpdateOnUiThread(Action action)
    {
        if (Application.Current?.Dispatcher.CheckAccess() == true)
            action();
        else
            Application.Current?.Dispatcher.InvokeAsync(action);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _channel?.Dispose();
    }
}

/// <summary>Represents a single action execution row in the Agent Monitor grid.</summary>
public sealed partial class AgentActionRow : ObservableObject
{
    [ObservableProperty] private DateTime _startTime;
    [ObservableProperty] private string _command = "";
    [ObservableProperty] private string _statusText = "Pending";
    [ObservableProperty] private string _statusColor = "#9399B2";
    [ObservableProperty] private int _exitCode;
    [ObservableProperty] private TimeSpan _duration;
}
