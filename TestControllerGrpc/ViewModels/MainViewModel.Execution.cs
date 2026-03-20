using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Helpers;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels;

// ?? Execution: Trigger, Execute, Cancel, Reset, Retry ???????????????
public sealed partial class MainViewModel
{
    private bool CanTriggerEvent => SelectedNode?.NodeKind == NodeKinds.Event && !IsExecuting;

    /// <summary>Trigger a single Event node's pipeline.</summary>
    [RelayCommand(CanExecute = nameof(CanTriggerEvent))]
    private async Task TriggerEvent()
    {
        if (SelectedNode?.ModelObject is not EventConfig ev) return;
        if (IsExecuting) { AddLog("Execution already in progress"); return; }

        WriteBackAll();

        // Find parent WatchItem for context
        var wiNode = SelectedNode.Parent;
        var wiConfig = wiNode?.ModelObject as WatchItemConfig;
        var ctx = new PipelineExecutionContext
        {
            WatchItemPath = wiConfig?.Path ?? "",
            TriggerFileName = $"[ManualTrigger:{ev.Type}]",
        };

        var eventNode = SelectedNode;
        IsExecuting = true;
        _executionCts = new CancellationTokenSource();

        // Mark event subtree as Running
        eventNode.SetStatusRecursive("Running");
        eventNode.PropagateStatusUp();
        AddLog($"Triggered Event: {ev.Type} on {wiConfig?.Tag ?? "?"}");

        try
        {
            await _executor.ExecuteEventAsync(ev, ctx, _executionCts.Token);
            eventNode.ExecutionStatus = "Success";
            eventNode.PropagateStatusUp();
            AddLog($"Event completed: {ev.Type}", LogSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            eventNode.SetFailed("Cancelled by user");
            eventNode.PropagateStatusUp();
            AddLog($"Event cancelled: {ev.Type}", LogSeverity.Warning);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Event execution failed: {EventType}", ev.Type);
            eventNode.SetFailed(ex.Message);
            eventNode.PropagateStatusUp();
            AddLog($"Event failed: {ev.Type} — {ex.Message}", LogSeverity.Error);
            ScrollLogToLastError();
        }
        finally
        {
            IsExecuting = false;
            _executionCts?.Dispose();
            _executionCts = null;
        }
    }

    private bool CanTriggerWatchItem => SelectedNode?.NodeKind == NodeKinds.WatchItem && !IsExecuting;

    /// <summary>Trigger ALL events on the selected WatchItem.</summary>
    [RelayCommand(CanExecute = nameof(CanTriggerWatchItem))]
    private async Task TriggerWatchItem()
    {
        if (SelectedNode?.ModelObject is not WatchItemConfig wi) return;
        if (IsExecuting) { AddLog("Execution already in progress"); return; }

        WriteBackAll();

        var wiNode = SelectedNode;
        IsExecuting = true;
        _executionCts = new CancellationTokenSource();

        // Mark entire WatchItem subtree as Running
        wiNode.SetStatusRecursive("Running");
        wiNode.PropagateStatusUp();
        AddLog($"Triggered WatchItem: {wi.Tag} ({wi.Events.Count} events)");

        var allSuccess = true;
        try
        {
            foreach (var ev in wi.Events)
            {
                var ctx = new PipelineExecutionContext
                {
                    WatchItemPath = wi.Path,
                    TriggerFileName = $"[ManualTrigger:{ev.Type}]",
                };
                // Find event node in tree to mark individually
                var evNode = wiNode.Children.FirstOrDefault(c =>
                    ReferenceEquals(c.ModelObject, ev));
                if (evNode is not null) evNode.SetStatusRecursive("Running");

                try
                {
                    await _executor.ExecuteEventAsync(ev, ctx, _executionCts.Token);
                    if (evNode is not null)
                    {
                        evNode.ExecutionStatus = "Success";
                        evNode.PropagateStatusUp();
                    }
                }
                catch (Exception ex)
                {
                    allSuccess = false;
                    if (evNode is not null)
                    {
                        evNode.SetFailed(ex.Message);
                        evNode.PropagateStatusUp();
                    }
                    AddLog($"Event failed: {ev.Type} — {ex.Message}", LogSeverity.Error);
                }
            }
            wiNode.ExecutionStatus = allSuccess ? "Success" : "Failed";
            wiNode.PropagateStatusUp();
            AddLog($"WatchItem {(allSuccess ? "completed" : "completed with errors")}: {wi.Tag}",
                allSuccess ? LogSeverity.Success : LogSeverity.Error);
            if (!allSuccess) ScrollLogToLastError();
        }
        catch (OperationCanceledException)
        {
            wiNode.SetFailed("Cancelled by user");
            wiNode.PropagateStatusUp();
            AddLog($"WatchItem cancelled: {wi.Tag}", LogSeverity.Warning);
        }
        finally
        {
            IsExecuting = false;
            _executionCts?.Dispose();
            _executionCts = null;
        }
    }

    private bool CanCancelExecution => IsExecuting;

    /// <summary>Cancel running execution.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelExecution))]
    private void CancelExecution()
    {
        _executionCts?.Cancel();
        AddLog("Cancellation requested");
    }

    /// <summary>Reset all execution status indicators to Idle.</summary>
    [RelayCommand]
    private void ResetStatus()
    {
        WatchListRoot?.ResetStatus();
        AddLog("Execution status reset");
    }

    private bool CanTriggerAllWatchItems => !IsExecuting;

    /// <summary>Trigger ALL WatchItems simultaneously (parallel or sequential per config).</summary>
    [RelayCommand(CanExecute = nameof(CanTriggerAllWatchItems))]
    private async Task TriggerAllWatchItems()
    {
        if (_config.WatchItems.Count == 0) { AddLog("No WatchItems to execute"); return; }

        WriteBackAll();
        IsExecuting = true;
        _executionCts = new CancellationTokenSource();

        WatchListRoot?.SetStatusRecursive("Running");
        AddLog($"Triggered ALL WatchItems ({_config.WatchItems.Count} items)");

        var allSuccess = true;
        try
        {
            foreach (var wi in _config.WatchItems)
            {
                if (!wi.IsEnabled) continue;

                var wiNode = WatchListRoot?.Children.FirstOrDefault(c =>
                    ReferenceEquals(c.ModelObject, wi));
                if (wiNode is not null) wiNode.SetStatusRecursive("Running");

                var wiSuccess = true;
                foreach (var ev in wi.Events)
                {
                    var ctx = new PipelineExecutionContext
                    {
                        WatchItemPath = wi.Path,
                        TriggerFileName = $"[ManualTriggerAll:{ev.Type}]",
                    };
                    try
                    {
                        await _executor.ExecuteEventAsync(ev, ctx, _executionCts.Token);
                    }
                    catch (Exception ex)
                    {
                        wiSuccess = false;
                        AddLog($"Event failed: {wi.Tag}/{ev.Type} — {ex.Message}", LogSeverity.Error);
                    }
                }
                if (wiNode is not null)
                {
                    wiNode.ExecutionStatus = wiSuccess ? "Success" : "Failed";
                    if (!wiSuccess) wiNode.FailureMessage = "One or more events failed";
                    wiNode.PropagateStatusUp();
                }
                if (!wiSuccess) allSuccess = false;
            }

            if (WatchListRoot is not null)
                WatchListRoot.ExecutionStatus = allSuccess ? "Success" : "Failed";
            AddLog($"All WatchItems {(allSuccess ? "completed" : "completed with errors")}",
                allSuccess ? LogSeverity.Success : LogSeverity.Error);
            if (!allSuccess) ScrollLogToLastError();
        }
        catch (OperationCanceledException)
        {
            WatchListRoot?.SetStatusRecursive("Failed");
            AddLog("Execute All cancelled", LogSeverity.Warning);
        }
        finally
        {
            IsExecuting = false;
            _executionCts?.Dispose();
            _executionCts = null;
        }
    }

    private bool CanExecuteGroup => SelectedNode?.NodeKind == NodeKinds.ActionGroup && !IsExecuting;

    /// <summary>Execute a single ActionGroup and its children.</summary>
    [RelayCommand(CanExecute = nameof(CanExecuteGroup))]
    private async Task ExecuteGroup()
    {
        if (SelectedNode?.ModelObject is not ActionGroupConfig ag) return;
        if (IsExecuting) { AddLog("Execution already in progress"); return; }

        WriteBackAll();

        // Walk up to find the parent WatchItem for context
        var wiConfig = FindAncestorModel<WatchItemConfig>(SelectedNode);
        var ctx = new PipelineExecutionContext
        {
            WatchItemPath = wiConfig?.Path ?? "",
            TriggerFileName = $"[ManualTrigger:Group:{ag.Tag}]",
        };

        var groupNode = SelectedNode;
        IsExecuting = true;
        _executionCts = new CancellationTokenSource();

        groupNode.SetStatusRecursive("Running");
        groupNode.PropagateStatusUp();
        AddLog($"Triggered ActionGroup: {ag.Tag}");

        try
        {
            await _executor.ExecuteGroupAsync(ag, ctx, _executionCts.Token);
            groupNode.ExecutionStatus = "Success";
            groupNode.PropagateStatusUp();
            AddLog($"ActionGroup completed: {ag.Tag}", LogSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            groupNode.SetFailed("Cancelled by user");
            groupNode.PropagateStatusUp();
            AddLog($"ActionGroup cancelled: {ag.Tag}", LogSeverity.Warning);
        }
        catch (Exception ex)
        {
            groupNode.SetFailed(ex.Message);
            groupNode.PropagateStatusUp();
            AddLog($"ActionGroup failed: {ag.Tag} — {ex.Message}", LogSeverity.Error);
            ScrollLogToLastError();
        }
        finally
        {
            IsExecuting = false;
            _executionCts?.Dispose();
            _executionCts = null;
        }
    }

    private bool CanExecuteSingleAction => SelectedNode?.NodeKind == NodeKinds.Action && !IsExecuting;

    /// <summary>Execute a single Action node.</summary>
    [RelayCommand(CanExecute = nameof(CanExecuteSingleAction))]
    private async Task ExecuteSingleAction()
    {
        if (SelectedNode?.ModelObject is not ActionConfig action) return;
        if (IsExecuting) { AddLog("Execution already in progress"); return; }

        WriteBackAll();

        var wiConfig = FindAncestorModel<WatchItemConfig>(SelectedNode);
        var ctx = new PipelineExecutionContext
        {
            WatchItemPath = wiConfig?.Path ?? "",
            TriggerFileName = $"[ManualTrigger:Action:{action.Command}]",
        };

        var actionNode = SelectedNode;
        IsExecuting = true;
        _executionCts = new CancellationTokenSource();

        actionNode.ExecutionStatus = "Running";
        actionNode.PropagateStatusUp();
        AddLog($"Triggered Action: {action.Type} — {action.Command}");

        try
        {
            await _executor.ExecuteSingleActionAsync(action, ctx, _executionCts.Token);
            actionNode.ExecutionStatus = "Success";
            actionNode.PropagateStatusUp();
            AddLog($"Action completed: {action.Command}", LogSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            actionNode.SetFailed("Cancelled by user");
            actionNode.PropagateStatusUp();
            AddLog($"Action cancelled: {action.Command}", LogSeverity.Warning);
        }
        catch (Exception ex)
        {
            actionNode.SetFailed(ex.Message);
            actionNode.PropagateStatusUp();
            AddLog($"Action failed: {action.Command} — {ex.Message}", LogSeverity.Error);
            ScrollLogToLastError();
        }
        finally
        {
            IsExecuting = false;
            _executionCts?.Dispose();
            _executionCts = null;
        }
    }

    /// <summary>
    /// Re-executes only the actions that failed in the last execution session
    /// for the selected WatchItem, using the same resolved parameters.
    /// </summary>
    [RelayCommand]
    private async Task RetryFailed()
    {
        // Find the WatchItem tag from the selected node
        var watchItemTag = SelectedNode?.NodeKind switch
        {
            NodeKinds.WatchItem => SelectedNode?.Tag,
            NodeKinds.Event => SelectedNode?.Parent?.Tag,
            _ => null
        };

        if (string.IsNullOrEmpty(watchItemTag))
        {
            AddLog("Select a WatchItem or Event to retry failed actions.");
            return;
        }

        var lastSession = _sessionManager.GetLastSession(watchItemTag);
        if (lastSession is null)
        {
            AddLog($"No previous execution found for '{watchItemTag}'.");
            return;
        }

        if (lastSession.FailedCount == 0)
        {
            AddLog($"No failed actions to retry in '{watchItemTag}'.");
            return;
        }

        if (IsExecuting) { AddLog("Execution already in progress"); return; }

        IsExecuting = true;
        _executionCts = new CancellationTokenSource();
        AddLog($"{LogIcons.Info} Retrying {lastSession.FailedCount} failed action(s) for '{watchItemTag}'...");

        try
        {
            await _executor.RetryFailedAsync(lastSession, _executionCts.Token);

            var retrySession = _sessionManager.GetLastSession(watchItemTag);
            if (retrySession?.FailedCount == 0)
                AddLog($"{LogIcons.Success} Retry complete: all actions succeeded.", LogSeverity.Success);
            else
                AddLog($"{LogIcons.Warning} Retry complete: {retrySession?.FailedCount} action(s) still failing.", LogSeverity.Warning);
        }
        catch (OperationCanceledException)
        {
            AddLog("Retry cancelled by user.", LogSeverity.Warning);
        }
        catch (Exception ex)
        {
            AddLog($"{LogIcons.Error} Retry failed: {ex.Message}", LogSeverity.Error);
        }
        finally
        {
            IsExecuting = false;
            _executionCts?.Dispose();
            _executionCts = null;
        }
    }

    /// <summary>Walks up the tree to find the nearest ancestor with the given model type.</summary>
    private static T? FindAncestorModel<T>(TreeNodeViewModel node) where T : class
    {
        var current = node.Parent;
        while (current is not null)
        {
            if (current.ModelObject is T model) return model;
            current = current.Parent;
        }
        return null;
    }

    /// <summary>Changes the ExecutionType of the selected Event or ActionGroup node.</summary>
    public void ChangeExecutionType(TreeNodeViewModel node, ExecutionMode newMode)
    {
        node.ExecutionTypeText = newMode.ToString();
        node.ApplyToModel();
        node.RefreshDisplayText();
        AddLog($"Changed ExecutionType of '{node.DisplayText}' to {newMode}");
    }
}
