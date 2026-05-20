using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Helpers;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels.Execution;

namespace TestControllerGrpc.ViewModels;

// ?? Execution: Trigger, Execute, Cancel, Reset, Retry ???????????????
public sealed partial class MainViewModel
{
    private bool CanTriggerEvent => SelectedNode?.NodeKind == NodeKinds.Event;

    /// <summary>Trigger a single Event node's pipeline.</summary>
    [RelayCommand(CanExecute = nameof(CanTriggerEvent))] 
    private async Task TriggerEvent()
    {
        if (SelectedNode?.ModelObject is not EventConfig ev) return;

        var wiNode = SelectedNode.Parent;
        var wiConfig = wiNode?.ModelObject as WatchItemConfig;
        var tag = wiConfig?.Tag ?? "Event";

        if (IsWatchItemRunning(tag))
        {
            AddLog($"WatchItem '{tag}' is already running");
            return;
        }

        WriteBackAll();

        var session = CreateSession(tag);
        var eventNode = SelectedNode;

        var ctx = new PipelineExecutionContext
        {
            WatchItemPath = wiConfig?.Path ?? "",
            TriggerFileName = $"[ManualTrigger:{ev.Type}]",
            Parameters = CollectInitializeParameters(SelectedNode),
            SessionId = session.SessionId,
        };

        // Acquire agent locks
        var requiredAgents = wiConfig != null
            ? AgentResolver.ExtractAgentNames(ev, ctx.Parameters)
            : [];
        if (requiredAgents.Count > 0)
        {
            var wpfUser = $"WPF/{Environment.UserName}@{Environment.MachineName}";
            var (locked, conflicts) = _lockManager.TryLockAgents(
                requiredAgents, session.SessionId, tag, wpfUser, "WPF");
            if (!locked)
            {
                var conflictMsg = string.Join("\n",
                    conflicts.Select(c => $"  {c.AgentName} � locked by {c.UserId} ({c.WatchItemTag})"));
                AddLog($"Cannot start '{tag}' � agents are busy:\n{conflictMsg}", LogSeverity.Warning);
                Application.Current?.Dispatcher.Invoke(() => ActiveSessions.Remove(session));
                return;
            }
        }

        eventNode.SetStatusRecursive("Running");
        eventNode.PropagateStatusUp();
        AddLog($"[{session.SessionId}] Triggered Event: {ev.Type} on {tag}");

        _events.Publish(new ExecutionStartedEvent(session.SessionId, tag, ev.Type, "WPF"));

        try
        {
            await _executor.ExecuteEventTrackedAsync(tag, ev, ctx, session.Cts.Token);
            eventNode.ExecutionStatus = "Success";
            eventNode.PropagateStatusUp();
            AddLog($"[{session.SessionId}] Event completed: {ev.Type}", LogSeverity.Success);
            var execSession = _sessionManager.GetSession(session.SessionId)
                ?? _sessionManager.GetLastSession(tag);
            _events.Publish(new ExecutionCompletedEvent(session.SessionId, tag, "Success",
                execSession?.SucceededCount ?? 0, execSession?.FailedCount ?? 0, execSession?.TotalActions ?? 0));
        }
        catch (OperationCanceledException)
        {
            eventNode.SetFailed("Cancelled by user");
            eventNode.PropagateStatusUp();
            AddLog($"[{session.SessionId}] Event cancelled: {ev.Type}", LogSeverity.Warning);
            _events.Publish(new ExecutionCompletedEvent(session.SessionId, tag, "Cancelled", 0, 0, 0));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Event execution failed: {EventType}", ev.Type);
            eventNode.SetFailed(ex.Message);
            eventNode.PropagateStatusUp();
            AddLog($"[{session.SessionId}] Event failed: {ev.Type} � {ex.Message}", LogSeverity.Error);
            ScrollLogToLastError();
            _events.Publish(new ExecutionCompletedEvent(session.SessionId, tag, "Failed", 0, 0, 0));
        }
        finally
        {
            _lockManager.ReleaseSession(session.SessionId);
            CompleteSession(session);
        }
    }

    private bool CanTriggerWatchItem => SelectedNode?.NodeKind == NodeKinds.WatchItem;

    /// <summary>Trigger ALL events on the selected WatchItem.</summary>
    [RelayCommand(CanExecute = nameof(CanTriggerWatchItem))]
    private async Task TriggerWatchItem()
    {
        if (SelectedNode?.ModelObject is not WatchItemConfig wi) return;

        if (IsWatchItemRunning(wi.Tag))
        {
            AddLog($"WatchItem '{wi.Tag}' is already running");
            return;
        }

        WriteBackAll();

        var session = CreateSession(wi.Tag);
        var wiNode = SelectedNode;

        // Acquire agent locks for all agents across all events
        var parameters = CollectInitializeParameters(SelectedNode);
        var requiredAgents = AgentResolver.ExtractAgentNames(wi, parameters);
        if (requiredAgents.Count > 0)
        {
            var wpfUser = $"WPF/{Environment.UserName}@{Environment.MachineName}";
            var (locked, conflicts) = _lockManager.TryLockAgents(
                requiredAgents, session.SessionId, wi.Tag, wpfUser, "WPF");
            if (!locked)
            {
                var conflictMsg = string.Join("\n",
                    conflicts.Select(c => $"  {c.AgentName} � locked by {c.UserId} ({c.WatchItemTag})"));
                AddLog($"Cannot start '{wi.Tag}' � agents are busy:\n{conflictMsg}", LogSeverity.Warning);
                Application.Current?.Dispatcher.Invoke(() => ActiveSessions.Remove(session));
                return;
            }
        }

        wiNode.SetStatusRecursive("Running");
        wiNode.PropagateStatusUp();
        AddLog($"[{session.SessionId}] Triggered WatchItem: {wi.Tag} ({wi.Events.Count} events)");

        _events.Publish(new ExecutionStartedEvent(session.SessionId, wi.Tag, "All", "WPF"));

        var allSuccess = true;
        try
        {
            foreach (var ev in wi.Events)
            {
                if (session.Cts.IsCancellationRequested) break;

                var ctx = new PipelineExecutionContext
                {
                    WatchItemPath = wi.Path,
                    TriggerFileName = $"[ManualTrigger:{ev.Type}]",
                    SessionId = session.SessionId,
                    Parameters = parameters,
                };
                var evNode = wiNode.Children.FirstOrDefault(c =>
                    ReferenceEquals(c.ModelObject, ev));
                if (evNode is not null) evNode.SetStatusRecursive("Running");

                try
                {
                    await _executor.ExecuteEventTrackedAsync(wi.Tag, ev, ctx, session.Cts.Token);
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
                    AddLog($"[{session.SessionId}] Event failed: {ev.Type} � {ex.Message}", LogSeverity.Error);
                }
            }
            wiNode.ExecutionStatus = allSuccess ? "Success" : "Failed";
            wiNode.PropagateStatusUp();
            AddLog($"[{session.SessionId}] WatchItem {(allSuccess ? "completed" : "completed with errors")}: {wi.Tag}",
                allSuccess ? LogSeverity.Success : LogSeverity.Error);
            if (!allSuccess) ScrollLogToLastError();
            var execSession = _sessionManager.GetSession(session.SessionId)
                ?? _sessionManager.GetLastSession(wi.Tag);
            _events.Publish(new ExecutionCompletedEvent(session.SessionId, wi.Tag, allSuccess ? "Success" : "Failed",
                execSession?.SucceededCount ?? 0, execSession?.FailedCount ?? 0, execSession?.TotalActions ?? 0));
        }
        catch (OperationCanceledException)
        {
            wiNode.SetFailed("Cancelled by user");
            wiNode.PropagateStatusUp();
            AddLog($"[{session.SessionId}] WatchItem cancelled: {wi.Tag}", LogSeverity.Warning);
            _events.Publish(new ExecutionCompletedEvent(session.SessionId, wi.Tag, "Cancelled", 0, 0, 0));
        }
        finally
        {
            _lockManager.ReleaseSession(session.SessionId);
            CompleteSession(session);
        }
    }

    private bool CanCancelExecution => ActiveSessions.Any(s => s.Status == "Running");

    /// <summary>Cancel all running executions.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelExecution))]
    private void CancelExecution()
    {
        foreach (var session in ActiveSessions.Where(s => s.Status == "Running").ToList())
        {
            session.Cancel();
            AddLog($"[{session.SessionId}] Cancellation requested for {session.WatchItemTag}");
        }
        _executionCts?.Cancel();
    }

    /// <summary>
    /// Cancel a specific pipeline session.
    /// Phase 1.12: funnels through <c>CancelSessionRequestEvent</c> so every
    /// cancel request � from the tree context menu, the multi-session
    /// dashboard, or any future caller � flows through the single handler
    /// in <c>MainViewModel.cs</c> that owns the <c>PipelineSession</c> CTS.
    /// </summary>
    [RelayCommand]
    private void CancelSession(PipelineSession? session)
    {
        if (session is null) return;
        _events.Publish(new CancelSessionRequestEvent { SessionId = session.SessionId });
    }

    /// <summary>Reset all execution status indicators to Idle.</summary>
    [RelayCommand]
    private void ResetStatus()
    {
        WatchListRoot?.ResetStatus();
        AddLog("Execution status reset");
    }

    private bool CanTriggerAllWatchItems => SelectedNode?.NodeKind == NodeKinds.WatchList;

    /// <summary>
    /// Trigger ALL WatchItems in parallel.
    /// Each WatchItem gets its own PipelineSession and runs concurrently.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTriggerAllWatchItems))]
    private async Task TriggerAllWatchItems()
    {
        if (_config.WatchItems.Count == 0) { AddLog("No WatchItems to execute"); return; }

        WriteBackAll();

        WatchListRoot?.SetStatusRecursive("Running");

        var enabledItems = _config.WatchItems.Where(wi => wi.IsEnabled).ToList();
        AddLog($"Triggered ALL WatchItems ({enabledItems.Count} enabled items) � parallel");

        var tasks = enabledItems.Select(async wi =>
        {
            if (IsWatchItemRunning(wi.Tag))
            {
                AddLog($"WatchItem '{wi.Tag}' is already running � skipping");
                return true;
            }

            var session = CreateSession(wi.Tag);
            var wiNode = WatchListRoot?.Children.FirstOrDefault(c =>
                ReferenceEquals(c.ModelObject, wi));

            await Application.Current!.Dispatcher.InvokeAsync(() =>
            {
                if (wiNode is not null) wiNode.SetStatusRecursive("Running");
            });

            _events.Publish(new ExecutionStartedEvent(session.SessionId, wi.Tag, "All", "WPF"));

            var wiSuccess = true;
            try
            {
                foreach (var ev in wi.Events)
                {
                    if (session.Cts.IsCancellationRequested) break;

                    var ctx = new PipelineExecutionContext
                    {
                        WatchItemPath = wi.Path,
                        TriggerFileName = $"[ManualTriggerAll:{ev.Type}]",
                        SessionId = session.SessionId,
                    };
                    try
                    {
                        await _executor.ExecuteEventTrackedAsync(wi.Tag, ev, ctx, session.Cts.Token);
                    }
                    catch (Exception ex)
                    {
                        wiSuccess = false;
                        AddLog($"[{session.SessionId}] Event failed: {wi.Tag}/{ev.Type} � {ex.Message}", LogSeverity.Error);
                    }
                }

                await Application.Current!.Dispatcher.InvokeAsync(() =>
                {
                    if (wiNode is not null)
                    {
                        wiNode.ExecutionStatus = wiSuccess ? "Success" : "Failed";
                        if (!wiSuccess) wiNode.FailureMessage = "One or more events failed";
                        wiNode.PropagateStatusUp();
                    }
                });

                _events.Publish(new ExecutionCompletedEvent(
                    session.SessionId, wi.Tag, wiSuccess ? "Success" : "Failed", 0, 0, 0));
            }
            catch (OperationCanceledException)
            {
                await Application.Current!.Dispatcher.InvokeAsync(() =>
                {
                    if (wiNode is not null) wiNode.SetFailed("Cancelled");
                });
                _events.Publish(new ExecutionCompletedEvent(
                    session.SessionId, wi.Tag, "Cancelled", 0, 0, 0));
            }
            finally
            {
                Application.Current?.Dispatcher.Invoke(() => CompleteSession(session));
            }

            return wiSuccess;
        });

        var results = await Task.WhenAll(tasks);
        var allSuccess = results.All(r => r);

        if (WatchListRoot is not null)
            WatchListRoot.ExecutionStatus = allSuccess ? "Success" : "Failed";
        AddLog($"All WatchItems {(allSuccess ? "completed" : "completed with errors")}",
            allSuccess ? LogSeverity.Success : LogSeverity.Error);
        if (!allSuccess) ScrollLogToLastError();
    }

    private bool CanExecuteGroup => SelectedNode?.NodeKind == NodeKinds.ActionGroup;

    /// <summary>Execute a single ActionGroup and its children.</summary>
    [RelayCommand(CanExecute = nameof(CanExecuteGroup))]
    private async Task ExecuteGroup()
    {
        if (SelectedNode?.ModelObject is not ActionGroupConfig ag) return;

        var tag = FindWatchItemTag(SelectedNode) ?? ag.Tag;
        if (IsWatchItemRunning(tag))
        {
            AddLog($"WatchItem '{tag}' is already running");
            return;
        }

        WriteBackAll();

        var session = CreateSession(tag);
        var wiConfig = FindAncestorModel<WatchItemConfig>(SelectedNode);
        var ctx = new PipelineExecutionContext
        {
            WatchItemPath = wiConfig?.Path ?? "",
            TriggerFileName = $"[ManualTrigger:Group:{ag.Tag}]",
            Parameters = CollectInitializeParameters(SelectedNode),
            SessionId = session.SessionId,
        };

        // Acquire agent locks so Registry/Monitor reflect live status
        var requiredAgents = AgentResolver.ExtractAgentNames(ag, ctx.Parameters);
        if (requiredAgents.Count > 0)
        {
            var wpfUser = $"WPF/{Environment.UserName}@{Environment.MachineName}";
            var (locked, conflicts) = _lockManager.TryLockAgents(
                requiredAgents, session.SessionId, tag, wpfUser, "WPF");
            if (!locked)
            {
                var conflictMsg = string.Join("\n",
                    conflicts.Select(c => $"  {c.AgentName} \u2190 locked by {c.UserId} ({c.WatchItemTag})"));
                AddLog($"Cannot start group '{ag.Tag}' \u2014 agents are busy:\n{conflictMsg}", LogSeverity.Warning);
                Application.Current?.Dispatcher.Invoke(() => ActiveSessions.Remove(session));
                return;
            }
            _events.Publish(new AgentLocksChangedEvent
            {
                Locks = _lockManager.GetAllLocks()
                    .Select(l => new AgentLockInfo
                    {
                        AgentName = l.AgentName, SessionId = l.SessionId,
                        WatchItemTag = l.WatchItemTag, UserId = l.UserId,
                        Source = l.Source, LockedAtUtc = l.LockedAtUtc,
                    }).ToList(),
                Reason = $"Group execution: {ag.Tag}",
            });
        }

        var groupNode = SelectedNode;
        groupNode.SetStatusRecursive("Running");
        groupNode.PropagateStatusUp();
        AddLog($"[{session.SessionId}] Triggered ActionGroup: {ag.Tag}");

        _events.Publish(new ExecutionStartedEvent(session.SessionId, tag, $"Group:{ag.Tag}", "WPF"));

        try
        {
            await _executor.ExecuteGroupTrackedAsync(tag, ag, ctx, session.Cts.Token);
            groupNode.ExecutionStatus = "Success";
            groupNode.PropagateStatusUp();
            AddLog($"[{session.SessionId}] ActionGroup completed: {ag.Tag}", LogSeverity.Success);
            var execSession = _sessionManager.GetSession(session.SessionId)
                ?? _sessionManager.GetLastSession(tag);
            _events.Publish(new ExecutionCompletedEvent(session.SessionId, tag, "Success",
                execSession?.SucceededCount ?? 0, execSession?.FailedCount ?? 0, execSession?.TotalActions ?? 0));
        }
        catch (OperationCanceledException)
        {
            groupNode.SetFailed("Cancelled by user");
            groupNode.PropagateStatusUp();
            AddLog($"[{session.SessionId}] ActionGroup cancelled: {ag.Tag}", LogSeverity.Warning);
            _events.Publish(new ExecutionCompletedEvent(session.SessionId, tag, "Cancelled", 0, 0, 0));
        }
        catch (Exception ex)
        {
            groupNode.SetFailed(ex.Message);
            groupNode.PropagateStatusUp();
            AddLog($"[{session.SessionId}] ActionGroup failed: {ag.Tag} � {ex.Message}", LogSeverity.Error);
            ScrollLogToLastError();
            _events.Publish(new ExecutionCompletedEvent(session.SessionId, tag, "Failed", 0, 0, 0));
        }
        finally
        {
            _lockManager.ReleaseSession(session.SessionId);
            _events.Publish(new AgentLocksChangedEvent
            {
                Locks = _lockManager.GetAllLocks()
                    .Select(l => new AgentLockInfo
                    {
                        AgentName = l.AgentName, SessionId = l.SessionId,
                        WatchItemTag = l.WatchItemTag, UserId = l.UserId,
                        Source = l.Source, LockedAtUtc = l.LockedAtUtc,
                    }).ToList(),
                Reason = $"Group completed: {ag.Tag}",
            });
            CompleteSession(session);
        }
    }

    private bool CanExecuteSingleAction => SelectedNode?.NodeKind == NodeKinds.Action;

    /// <summary>Execute a single Action node.</summary>
    [RelayCommand(CanExecute = nameof(CanExecuteSingleAction))]
    private async Task ExecuteSingleAction()
    {
        if (SelectedNode?.ModelObject is not ActionConfig action) return;

        var tag = FindWatchItemTag(SelectedNode) ?? action.Command;
        if (IsWatchItemRunning(tag))
        {
            AddLog($"WatchItem '{tag}' is already running");
            return;
        }

        WriteBackAll();

        var session = CreateSession(tag);
        var wiConfig = FindAncestorModel<WatchItemConfig>(SelectedNode);
        var ctx = new PipelineExecutionContext
        {
            WatchItemPath = wiConfig?.Path ?? "",
            TriggerFileName = $"[ManualTrigger:Action:{action.Command}]",
            Parameters = CollectInitializeParameters(SelectedNode),
            SessionId = session.SessionId,
        };

        // Acquire agent lock so Registry/Monitor reflect live status
        var requiredAgents = AgentResolver.ExtractAgentNames(action, ctx.Parameters);
        if (requiredAgents.Count > 0)
        {
            var wpfUser = $"WPF/{Environment.UserName}@{Environment.MachineName}";
            var (locked, conflicts) = _lockManager.TryLockAgents(
                requiredAgents, session.SessionId, tag, wpfUser, "WPF");
            if (!locked)
            {
                var conflictMsg = string.Join("\n",
                    conflicts.Select(c => $"  {c.AgentName} \u2190 locked by {c.UserId} ({c.WatchItemTag})"));
                AddLog($"Cannot start action '{action.ResolvedTag}' \u2014 agent is busy:\n{conflictMsg}", LogSeverity.Warning);
                Application.Current?.Dispatcher.Invoke(() => ActiveSessions.Remove(session));
                return;
            }
            _events.Publish(new AgentLocksChangedEvent
            {
                Locks = _lockManager.GetAllLocks()
                    .Select(l => new AgentLockInfo
                    {
                        AgentName = l.AgentName, SessionId = l.SessionId,
                        WatchItemTag = l.WatchItemTag, UserId = l.UserId,
                        Source = l.Source, LockedAtUtc = l.LockedAtUtc,
                    }).ToList(),
                Reason = $"Action execution: {action.ResolvedTag}",
            });
        }

        var actionNode = SelectedNode;
        actionNode.ExecutionStatus = "Running";
        actionNode.PropagateStatusUp();
        AddLog($"[{session.SessionId}] Triggered Action: {action.Type} � {action.Command}");

        _events.Publish(new ExecutionStartedEvent(session.SessionId, tag, $"Action:{action.ResolvedTag}", "WPF"));

        try
        {
            await _executor.ExecuteSingleActionTrackedAsync(tag, action, ctx, session.Cts.Token);
            actionNode.ExecutionStatus = "Success";
            actionNode.PropagateStatusUp();
            AddLog($"[{session.SessionId}] Action completed: {action.Command}", LogSeverity.Success);
            var execSession = _sessionManager.GetSession(session.SessionId)
                ?? _sessionManager.GetLastSession(tag);
            _events.Publish(new ExecutionCompletedEvent(session.SessionId, tag, "Success",
                execSession?.SucceededCount ?? 0, execSession?.FailedCount ?? 0, execSession?.TotalActions ?? 0));
        }
        catch (OperationCanceledException)
        {
            actionNode.SetFailed("Cancelled by user");
            actionNode.PropagateStatusUp();
            AddLog($"[{session.SessionId}] Action cancelled: {action.Command}", LogSeverity.Warning);
            _events.Publish(new ExecutionCompletedEvent(session.SessionId, tag, "Cancelled", 0, 0, 0));
        }
        catch (Exception ex)
        {
            actionNode.SetFailed(ex.Message);
            actionNode.PropagateStatusUp();
            AddLog($"[{session.SessionId}] Action failed: {action.Command} � {ex.Message}", LogSeverity.Error);
            ScrollLogToLastError();
            _events.Publish(new ExecutionCompletedEvent(session.SessionId, tag, "Failed", 0, 0, 0));
        }
        finally
        {
            _lockManager.ReleaseSession(session.SessionId);
            _events.Publish(new AgentLocksChangedEvent
            {
                Locks = _lockManager.GetAllLocks()
                    .Select(l => new AgentLockInfo
                    {
                        AgentName = l.AgentName, SessionId = l.SessionId,
                        WatchItemTag = l.WatchItemTag, UserId = l.UserId,
                        Source = l.Source, LockedAtUtc = l.LockedAtUtc,
                    }).ToList(),
                Reason = $"Action completed: {action.ResolvedTag}",
            });
            CompleteSession(session);
        }
    }

    /// <summary>
    /// Re-executes only the actions that failed in the last execution session
    /// for the selected WatchItem, using the same resolved parameters.
    /// </summary>
    [RelayCommand]
    private async Task RetryFailed()
    {
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

        if (IsWatchItemRunning(watchItemTag))
        {
            AddLog($"WatchItem '{watchItemTag}' is already running");
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

        var session = CreateSession(watchItemTag);
        AddLog($"[{session.SessionId}] {LogIcons.Info} Retrying {lastSession.FailedCount} failed action(s) for '{watchItemTag}'...");

        try
        {
            await _executor.RetryFailedAsync(lastSession, session.Cts.Token);

            var retrySession = _sessionManager.GetLastSession(watchItemTag);
            if (retrySession?.FailedCount == 0)
                AddLog($"[{session.SessionId}] {LogIcons.Success} Retry complete: all actions succeeded.", LogSeverity.Success);
            else
                AddLog($"[{session.SessionId}] {LogIcons.Warning} Retry complete: {retrySession?.FailedCount} action(s) still failing.", LogSeverity.Warning);
        }
        catch (OperationCanceledException)
        {
            AddLog($"[{session.SessionId}] Retry cancelled by user.", LogSeverity.Warning);
        }
        catch (Exception ex)
        {
            AddLog($"[{session.SessionId}] {LogIcons.Error} Retry failed: {ex.Message}", LogSeverity.Error);
        }
        finally
        {
            CompleteSession(session);
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

    /// <summary>
    /// Walks up the tree from the selected node to find the WatchItem root,
    /// then collects parameters from all Initialize nodes in that subtree.
    /// This ensures tokens are resolved even when executing mid-tree.
    /// </summary>
    private Dictionary<string, string> CollectInitializeParameters(TreeNodeViewModel node)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Walk up to find the WatchItem root
        var current = node;
        TreeNodeViewModel? watchItemNode = null;
        while (current != null)
        {
            if (current.NodeKind == NodeKinds.WatchItem)
            {
                watchItemNode = current;
                break;
            }
            current = current.Parent;
        }

        if (watchItemNode == null) return parameters;

        // Use a temporary context to leverage the existing ParameterResolver
        var tempCtx = new PipelineExecutionContext();
        GatherInitializeParams(watchItemNode, tempCtx);

        if (tempCtx.Parameters.Count > 0)
        {
            AddLog($"Pre-loaded {tempCtx.Parameters.Count} parameters from Initialize nodes", LogSeverity.Info);
        }

        return tempCtx.Parameters;
    }

    /// <summary>
    /// Recursively walks the tree to find Initialize nodes and loads their
    /// parameter files into the execution context.
    /// </summary>
    private void GatherInitializeParams(TreeNodeViewModel node, PipelineExecutionContext ctx)
    {
        if (node.NodeKind == NodeKinds.Initialize
            && node.ModelObject is InitializeConfig init
            && !string.IsNullOrEmpty(init.ParameterFile))
        {
            var path = ParameterResolver.Resolve(init.ParameterFile, ctx);
            try
            {
                ParameterResolver.LoadParameterFile(ctx, path);
            }
            catch (Exception ex)
            {
                AddLog($"Failed to pre-load parameters from {path}: {ex.Message}", LogSeverity.Warning);
            }
        }

        foreach (var child in node.Children)
        {
            GatherInitializeParams(child, ctx);
        }
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
