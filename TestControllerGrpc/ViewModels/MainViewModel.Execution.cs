using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using TestController.Api.Contracts;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Helpers;
using TestControllerGrpc.Identity;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels.Execution;
using TestControllerGrpc.Views.Dialogs;

namespace TestControllerGrpc.ViewModels;

// ?? Execution: Trigger, Execute, Cancel, Reset, Retry ???????????????
public sealed partial class MainViewModel
{
    /// <summary>
    /// Resolves the current IUserContext for authorization checks.
    /// In Default mode, returns the synthetic Default WPF user.
    /// In Secured mode, builds context from AuthClient.CurrentUser.
    /// </summary>
    private IUserContext GetCurrentUserContext()
    {
        var authUser = _authClient.CurrentUser;
        if (authUser is null)
            return DefaultUser.ForClient(ClientKind.Wpf);

        return new SyntheticUserContext(
            userId: authUser.UserId,
            displayName: authUser.DisplayName,
            clientKind: ClientKind.Wpf,
            roles: [authUser.Role]);
    }

    /// <summary>
    /// Acquires the single-run pipeline lock for a WPF-initiated trigger.
    /// Single-run: ANY active lock (any user, any role, the owner and Administrator
    /// included) blocks the trigger — the only path forward is Cancel. Returns the
    /// per-acquisition token to thread into the execution context, or <c>null</c> when
    /// the pipeline already has an active run (the conflict dialog is shown).
    /// </summary>
    private string? AcquirePipelineLock(string tag)
    {
        var user = GetCurrentUserContext();
        var owner = new TestControllerGrpc.Locking.OwnerIdentity(user.UserId, user.DisplayName, ClientKind.Wpf);
        var result = _lockRegistry.TryAcquire(tag, owner, TestControllerGrpc.Locking.LockKind.Trigger);
        if (result is TestControllerGrpc.Locking.AcquireResult.Conflict conflict)
        {
            var dto = LockMapper.ToDto(conflict.ExistingLock);
            AddLog($"Pipeline '{tag}' is locked by {dto.OwnerDisplayName} ({dto.OwnerClientKind})", LogSeverity.Warning);
            ShowLockConflictDialog(dto);
            return null;
        }
        return (result as TestControllerGrpc.Locking.AcquireResult.Success)?.Lock.Token ?? string.Empty;
    }

    private bool CanTriggerEvent
    {
        get
        {
            if (SelectedNode?.NodeKind != NodeKinds.Event) return false;
            var tag = (SelectedNode.Parent?.ModelObject as WatchItemConfig)?.Tag;
            if (tag is not null && _lockStateService.HasActiveLock(tag)) return false;
            return _capabilityChecker.Can(Permission.Pipeline_Trigger, tag);
        }
    }

    /// <summary>Trigger a single Event node's pipeline.</summary>
    [RelayCommand(CanExecute = nameof(CanTriggerEvent), AllowConcurrentExecutions = true)] 
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

        // Phase 2a: Authorization guard — runs before lock acquisition
        try
        {
            await _pipelineGuard.AuthorizeAsync(GetCurrentUserContext(), Permission.Pipeline_Trigger, tag);
        }
        catch (PipelineAuthorizationDeniedException ex)
        {
            AddLog($"Trigger denied for '{tag}': {ex.Message}", LogSeverity.Warning);
            ShowAuthorizationDeniedDialog(ex.Message);
            return;
        }

        WriteBackAll();

        // Single-run pipeline lock — ANY active run blocks (owner included). Path is Cancel.
        var pipelineToken = AcquirePipelineLock(tag);
        if (pipelineToken is null) return;

        // Keep this run's lock alive so the expiry sweeper never reaps it mid-run.
        using var pipelineLockRenewal = new TestControllerGrpc.Locking.LockRenewalTimer(
            _lockRegistry, tag, pipelineToken, _lockRegistry.RenewalInterval);

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
                _lockRegistry.TryRelease(tag, pipelineToken);
                Application.Current?.Dispatcher.InvokeAsync(() => ActiveSessions.Remove(session));
                return;
            }            _events.Publish(new AgentLocksChangedEvent
            {
                Locks = _lockManager.GetAllLocks()
                    .Select(l => new AgentLockInfo
                    {
                        AgentName = l.AgentName, SessionId = l.SessionId,
                        WatchItemTag = l.WatchItemTag, UserId = l.UserId,
                        Source = l.Source, LockedAtUtc = l.LockedAtUtc,
                    }).ToList(),
                Reason = $"Event execution: {tag}",
            });        }

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
            eventNode.CancelWithDescendants();
            eventNode.PropagateStatusUp();
            AddLog($"[{session.SessionId}] Event cancelled: {ev.Type}", LogSeverity.Warning);
            _events.Publish(new ExecutionCompletedEvent(session.SessionId, tag, "Cancelled", 0, 0, 0));
        }
        catch (RpcException rpcEx) when (rpcEx.StatusCode == StatusCode.Aborted)
        {
            // Pipeline lock conflict — extract PipelineLockDto from trailers
            var lockDto = ExtractLockDtoFromTrailers(rpcEx);
            if (lockDto is not null)
            {
                AddLog($"Pipeline '{tag}' is locked by {lockDto.OwnerDisplayName} ({lockDto.OwnerClientKind})", LogSeverity.Warning);
                ShowLockConflictDialog(lockDto);
            }
            else
            {
                AddLog($"[{session.SessionId}] Trigger aborted: {rpcEx.Status.Detail}", LogSeverity.Warning);
            }
            eventNode.SetStatusRecursive("Idle");
            eventNode.PropagateStatusUp();
            _events.Publish(new ExecutionCompletedEvent(session.SessionId, tag, "Blocked", 0, 0, 0));
        }
        catch (Exception ex)
        {
            // Check for HTTP 409 conflict (REST path)
            if (ex is HttpRequestException httpEx && httpEx.StatusCode == HttpStatusCode.Conflict)
            {
                var lockDto = await ExtractLockDtoFromHttpExceptionAsync(ex);
                if (lockDto is not null)
                {
                    AddLog($"Pipeline '{tag}' is locked by {lockDto.OwnerDisplayName} ({lockDto.OwnerClientKind})", LogSeverity.Warning);
                    ShowLockConflictDialog(lockDto);
                    eventNode.SetStatusRecursive("Idle");
                    eventNode.PropagateStatusUp();
                    _events.Publish(new ExecutionCompletedEvent(session.SessionId, tag, "Blocked", 0, 0, 0));
                    return;
                }
            }

            _logger.LogError(ex, "Event execution failed: {EventType}", ev.Type);
            eventNode.SetFailed(ex.Message);
            eventNode.PropagateStatusUp();
            AddLog($"[{session.SessionId}] Event failed: {ev.Type} \u2014 {ex.Message}", LogSeverity.Error);
            ScrollLogToLastError();
            _events.Publish(new ExecutionCompletedEvent(session.SessionId, tag, "Failed", 0, 0, 0));
        }
        finally
        {
            _lockRegistry.TryRelease(tag, pipelineToken);
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
                Reason = $"Event completed: {tag}",
            });
            CompleteSession(session);
        }
    }

    private bool CanTriggerWatchItem
    {
        get
        {
            if (SelectedNode?.NodeKind != NodeKinds.WatchItem) return false;
            var tag = (SelectedNode.ModelObject as WatchItemConfig)?.Tag;
            if (tag is not null && _lockStateService.HasActiveLock(tag)) return false;
            return _capabilityChecker.Can(Permission.Pipeline_Trigger, tag);
        }
    }

    /// <summary>Trigger ALL events on the selected WatchItem.</summary>
    [RelayCommand(CanExecute = nameof(CanTriggerWatchItem), AllowConcurrentExecutions = true)]
    private async Task TriggerWatchItem()
    {
        if (SelectedNode?.ModelObject is not WatchItemConfig wi) return;

        if (IsWatchItemRunning(wi.Tag))
        {
            AddLog($"WatchItem '{wi.Tag}' is already running");
            return;
        }

        // Phase 2a: Authorization guard — runs before lock acquisition
        try
        {
            await _pipelineGuard.AuthorizeAsync(GetCurrentUserContext(), Permission.Pipeline_Trigger, wi.Tag);
        }
        catch (PipelineAuthorizationDeniedException ex)
        {
            AddLog($"Trigger denied for '{wi.Tag}': {ex.Message}", LogSeverity.Warning);
            ShowAuthorizationDeniedDialog(ex.Message);
            return;
        }

        WriteBackAll();

        // Single-run pipeline lock — ANY active run blocks (owner included). Path is Cancel.
        var pipelineToken = AcquirePipelineLock(wi.Tag);
        if (pipelineToken is null) return;

        // Keep this run's lock alive so the expiry sweeper never reaps it mid-run.
        using var pipelineLockRenewal = new TestControllerGrpc.Locking.LockRenewalTimer(
            _lockRegistry, wi.Tag, pipelineToken, _lockRegistry.RenewalInterval);

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
                _lockRegistry.TryRelease(wi.Tag, pipelineToken);
                Application.Current?.Dispatcher.InvokeAsync(() => ActiveSessions.Remove(session));
                return;
            }            _events.Publish(new AgentLocksChangedEvent
            {
                Locks = _lockManager.GetAllLocks()
                    .Select(l => new AgentLockInfo
                    {
                        AgentName = l.AgentName, SessionId = l.SessionId,
                        WatchItemTag = l.WatchItemTag, UserId = l.UserId,
                        Source = l.Source, LockedAtUtc = l.LockedAtUtc,
                    }).ToList(),
                Reason = $"WatchItem execution: {wi.Tag}",
            });        }

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
            wiNode.CancelWithDescendants();
            wiNode.PropagateStatusUp();
            AddLog($"[{session.SessionId}] WatchItem cancelled: {wi.Tag}", LogSeverity.Warning);
            _events.Publish(new ExecutionCompletedEvent(session.SessionId, wi.Tag, "Cancelled", 0, 0, 0));
        }
        finally
        {
            _lockRegistry.TryRelease(wi.Tag, pipelineToken);
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
                Reason = $"WatchItem completed: {wi.Tag}",
            });
            CompleteSession(session);
        }
    }

    private bool CanCancelExecution =>
        ActiveSessions.Any(s => s.Status == "Running")
        && _capabilityChecker.Can(Permission.Pipeline_Cancel);

    /// <summary>Cancel all running executions.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelExecution))]
    private async Task CancelExecution()
    {
        var userContext = GetCurrentUserContext();
        foreach (var session in ActiveSessions.Where(s => s.Status == "Running").ToList())
        {
            // Phase 2a: Authorize cancel per pipeline
            try
            {
                await _pipelineGuard.AuthorizeAsync(userContext, Permission.Pipeline_Cancel, session.WatchItemTag);
            }
            catch (PipelineAuthorizationDeniedException ex)
            {
                AddLog($"Cancel denied for '{session.WatchItemTag}': {ex.Message}", LogSeverity.Warning);
                ShowAuthorizationDeniedDialog(ex.Message);
                continue;
            }

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

    private bool CanTriggerAllWatchItems =>
        SelectedNode?.NodeKind == NodeKinds.WatchList
        && _capabilityChecker.Can(Permission.Pipeline_Trigger);

    /// <summary>
    /// Trigger ALL WatchItems in parallel.
    /// Each WatchItem gets its own PipelineSession and runs concurrently.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTriggerAllWatchItems), AllowConcurrentExecutions = true)]
    private async Task TriggerAllWatchItems()
    {
        if (_config.WatchItems.Count == 0) { AddLog("No WatchItems to execute"); return; }

        WriteBackAll();

        var enabledItems = _config.WatchItems.Where(wi => wi.IsEnabled).ToList();

        // Only mark enabled WatchItem nodes as Running; disabled items stay Idle
        if (WatchListRoot is not null)
        {
            WatchListRoot.ExecutionStatus = "Running";
            foreach (var wi in enabledItems)
            {
                var node = WatchListRoot.Children.FirstOrDefault(c => ReferenceEquals(c.ModelObject, wi));
                node?.SetStatusRecursive("Running");
            }
        }
        AddLog($"Triggered ALL WatchItems ({enabledItems.Count} enabled items) � parallel");

        var tasks = enabledItems.Select(async wi =>
        {
            if (IsWatchItemRunning(wi.Tag))
            {
                AddLog($"WatchItem '{wi.Tag}' is already running — skipping");
                return true;
            }

            // Single-run pipeline lock — ANY active run blocks (owner included); skip if held.
            var pipelineToken = AcquirePipelineLock(wi.Tag);
            if (pipelineToken is null) return true; // skip locked pipeline, not a batch failure

            // Keep this run's lock alive so the expiry sweeper never reaps it mid-run.
            using var pipelineLockRenewal = new TestControllerGrpc.Locking.LockRenewalTimer(
                _lockRegistry, wi.Tag, pipelineToken, _lockRegistry.RenewalInterval);

            var session = CreateSession(wi.Tag);
            var wiNode = WatchListRoot?.Children.FirstOrDefault(c =>
                ReferenceEquals(c.ModelObject, wi));

            // Acquire agent locks so Fleet cards turn blue during execution
            var parameters = wiNode != null
                ? CollectInitializeParameters(wiNode)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var requiredAgents = AgentResolver.ExtractAgentNames(wi, parameters);
            if (requiredAgents.Count > 0)
            {
                var wpfUser = $"WPF/{Environment.UserName}@{Environment.MachineName}";
                var (locked, conflicts) = _lockManager.TryLockAgents(
                    requiredAgents, session.SessionId, wi.Tag, wpfUser, "WPF");
                if (!locked)
                {
                    var conflictMsg = string.Join(", ",
                        conflicts.Select(c => $"{c.AgentName}←{c.UserId}"));
                    AddLog($"Cannot start '{wi.Tag}' — agents busy: {conflictMsg}", LogSeverity.Warning);
                    _lockRegistry.TryRelease(wi.Tag, pipelineToken);
                    await Application.Current!.Dispatcher.InvokeAsync(() => ActiveSessions.Remove(session));
                    return true; // skip this WatchItem, not a failure of the batch
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
                    Reason = $"TriggerAll execution: {wi.Tag}",
                });
            }

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
                        Parameters = parameters,
                    };
                    try
                    {
                        await _executor.ExecuteEventTrackedAsync(wi.Tag, ev, ctx, session.Cts.Token);
                    }
                    catch (Exception ex)
                    {
                        wiSuccess = false;
                        AddLog($"[{session.SessionId}] Event failed: {wi.Tag}/{ev.Type} — {ex.Message}", LogSeverity.Error);
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
                _lockRegistry.TryRelease(wi.Tag, pipelineToken);
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
                    Reason = $"TriggerAll completed: {wi.Tag}",
                });
                await Application.Current!.Dispatcher.InvokeAsync(() => CompleteSession(session));
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
    [RelayCommand(CanExecute = nameof(CanExecuteGroup), AllowConcurrentExecutions = true)]
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
                Application.Current?.Dispatcher.InvokeAsync(() => ActiveSessions.Remove(session));
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
            groupNode.CancelWithDescendants();
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
    [RelayCommand(CanExecute = nameof(CanExecuteSingleAction), AllowConcurrentExecutions = true)]
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
                Application.Current?.Dispatcher.InvokeAsync(() => ActiveSessions.Remove(session));
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
            actionNode.CancelWithDescendants();
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

    private bool CanRetryFailed
    {
        get
        {
            var tag = SelectedNode?.NodeKind switch
            {
                NodeKinds.WatchItem => SelectedNode?.Tag,
                NodeKinds.Event => SelectedNode?.Parent?.Tag,
                _ => null
            };
            if (string.IsNullOrEmpty(tag)) return false;
            if (_lockStateService.HasActiveLock(tag)) return false;
            return _capabilityChecker.Can(Permission.Pipeline_Retry, tag);
        }
    }

    /// <summary>
    /// Re-executes only the actions that failed in the last execution session
    /// for the selected WatchItem, using the same resolved parameters.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRetryFailed))]
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

        // Phase 4: Authorization guard
        try
        {
            await _pipelineGuard.AuthorizeAsync(GetCurrentUserContext(), Permission.Pipeline_Retry, watchItemTag);
        }
        catch (PipelineAuthorizationDeniedException ex)
        {
            AddLog($"Retry denied for '{watchItemTag}': {ex.Message}", LogSeverity.Warning);
            ShowAuthorizationDeniedDialog(ex.Message);
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

    // ── Phase 2b: Authorization denied dialog ───────────────────────

    /// <summary>
    /// Shows the AuthorizationDeniedDialog on the UI thread.
    /// Triggers a background /api/auth/me refresh to update assignments.
    /// </summary>
    private void ShowAuthorizationDeniedDialog(string message)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var dialog = new Views.Dialogs.AuthorizationDeniedDialog(message, _authClient)
            {
                Owner = Application.Current.MainWindow
            };
            dialog.ShowDialog();
        });
    }

    // ── Phase 3b: Lock conflict helpers ──────────────────────────────

    /// <summary>
    /// Extracts PipelineLockDto from gRPC trailing metadata (key: lock-conflict-bin).
    /// </summary>
    private static PipelineLockDto? ExtractLockDtoFromTrailers(RpcException rpcEx)
    {
        var entry = rpcEx.Trailers?.Get("lock-conflict-bin");
        if (entry is null) return null;
        try
        {
            var json = System.Text.Encoding.UTF8.GetString(entry.ValueBytes);
            return System.Text.Json.JsonSerializer.Deserialize<PipelineLockDto>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts PipelineLockDto from an HTTP 409 response body (REST path).
    /// </summary>
    private static async Task<PipelineLockDto?> ExtractLockDtoFromHttpExceptionAsync(Exception ex)
    {
        // The REST proxy returns JSON { "error": "pipeline-locked", "lock": { ... } }
        // In practice the DTO is attached via the execution pipeline response;
        // this method handles the edge case where a raw HttpResponseMessage is available.
        _ = ex; // placeholder — actual extraction depends on how the HTTP error propagates
        await Task.CompletedTask;
        return null;
    }

    /// <summary>
    /// Shows the Lock Conflict dialog populated from the DTO.
    /// If the user chooses force-release, opens the reason dialog.
    /// Badge updates come from the PipelineLockStolen broadcast — no optimistic update.
    /// </summary>
    private void ShowLockConflictDialog(PipelineLockDto dto)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var conflictDialog = new LockConflictDialog
            {
                Owner = Application.Current.MainWindow
            };
            conflictDialog.Initialize(dto);
            conflictDialog.ShowDialog();

            if (conflictDialog.ForceReleaseChosen)
            {
                var reasonDialog = new ForceReleaseReasonDialog
                {
                    Owner = Application.Current.MainWindow
                };
                reasonDialog.Initialize(dto.PipelineId, dto.OwnerDisplayName);
                reasonDialog.ShowDialog();
                // On success, badge updates via PipelineLockStolen broadcast — no local update needed
            }
        });
    }
}
