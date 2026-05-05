using Microsoft.Extensions.Logging;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Phase 2.16 spike: shared orchestration for <see cref="IActionPipelineExecutor"/>
/// implementations. Owns every part of the pipeline except the host-specific
/// <see cref="ExecuteActionAsync"/> (local/remote/SendMail dispatch, smart-retry).
///
/// Concrete executors:
/// <list type="bullet">
///   <item><c>TestControllerGrpc.Services.ActionPipelineExecutor</c> (WPF host) �
///     adds Polly resilience, smart retry, TRX parsing and SendMail.</item>
///   <item><c>TestController.WebApi.Services.StandalonePipelineExecutor</c> (WebApi host) �
///     simple Local/Remote dispatch via <c>IAgentGrpcDispatcher</c>.</item>
/// </list>
/// Methods are <c>protected virtual</c> where useful overrides are plausible
/// (e.g. <see cref="ExecuteInitialize"/>) and <c>protected</c> otherwise.
/// </summary>
public abstract class PipelineExecutorBase : IActionPipelineExecutor
{
    protected readonly ExecutionSessionManager _sessionManager;
    protected readonly ILogger _logger;
    protected Dictionary<string, TemplateConfig> _templates =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised for every action/event in the pipeline.</summary>
    public event Action<PipelineLogEntry>? LogEntry;

    /// <summary>
    /// Raised when an IActionNode starts or finishes execution.
    /// Status: "Running", "Success", "Failed", "PartialFailure", "Cancelled".
    /// </summary>
    public event Action<IActionNode, string>? NodeProgress;

    /// <summary>
    /// Raised when an action node fails, providing exit code and error details.
    /// </summary>
    public event Action<IActionNode, int, string>? NodeFailed;

    protected PipelineExecutorBase(
        ExecutionSessionManager sessionManager,
        ILogger logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    /// <summary>
    /// Loads the template dictionary for Ref resolution.
    /// Called whenever the vocabulary is loaded/reloaded.
    /// </summary>
    public void LoadTemplates(IEnumerable<TemplateConfig> templates)
    {
        _templates = templates.ToDictionary(t => t.ID, StringComparer.OrdinalIgnoreCase);
        _logger.LogInformation("Loaded {Count} templates", _templates.Count);
    }

    // ?? Top-level entry points ????????????????????????????????????????????

    public async Task ExecuteEventAsync(
        EventConfig evt, PipelineExecutionContext ctx, CancellationToken ct)
    {
        Log("Event", $"Triggered: Type={evt.Type}, Exec={evt.ExecutionType}");
        await ExecuteChildrenAsync(evt.Children, evt.ExecutionType, true, ctx, ct);
        Log("Event", "Completed");
    }

    public async Task<bool> ExecuteGroupAsync(
        ActionGroupConfig group, PipelineExecutionContext ctx, CancellationToken ct)
    {
        Log("ActionGroup",
            $"[{group.Tag}] Mode={group.ExecutionType}, FailAndContinue={group.FailAndContinue}");

        var success = await ExecuteChildrenAsync(
            group.Children, group.ExecutionType, group.FailAndContinue, ctx, ct);

        Log("ActionGroup", $"[{group.Tag}] {(success ? "? Completed" : "? Failed")}");
        return success || group.FailAndContinue;
    }

    public async Task<bool> ExecuteSingleActionAsync(
        ActionConfig action, PipelineExecutionContext ctx, CancellationToken ct)
    {
        OnNodeProgress(action, "Running");
        bool success;
        try
        {
            success = await ExecuteActionAsync(action, ctx, ct);
        }
        catch (OperationCanceledException)
        {
            OnNodeProgress(action, "Cancelled");
            return false;
        }
        OnNodeProgress(action, success ? "Success" : "Failed");
        return success;
    }

    public async Task ExecuteEventTrackedAsync(
        string watchItemTag, EventConfig evt, PipelineExecutionContext ctx, CancellationToken ct)
    {
        // Snapshot isolation: clone the action tree so hot-reloads don't mutate in-flight nodes.
        var snapshotChildren = evt.Children.Select(DeepCloneNode).ToList();

        // Use the caller's sessionId if provided (e.g. from WebApi controller).
        var callerSessionId = !string.IsNullOrEmpty(ctx.SessionId) ? ctx.SessionId : null;

        var session = _sessionManager.BeginSession(
            watchItemTag, evt.Type,
            new Dictionary<string, string>(ctx.Parameters, StringComparer.OrdinalIgnoreCase),
            snapshotChildren,
            callerSessionId);

        ctx.SessionId = session.SessionId;
        Log("Session", $"Started {session.SessionId} for {watchItemTag}:{evt.Type}");

        // Link the external cancellation token with the session's own CTS so
        // both _sessionManager.CancelSession() and external cancellation work.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Cts.Token);

        try
        {
            await ExecuteChildrenTrackedAsync(
                snapshotChildren, evt.ExecutionType, true, ctx, session, linkedCts.Token);
        }
        finally
        {
            _sessionManager.CompleteSession(session.SessionId);
            Log("Session", $"Completed {session.SessionId}: {session.SummaryText}");
        }
    }

    public async Task<bool> ExecuteGroupTrackedAsync(
        string watchItemTag, ActionGroupConfig group, PipelineExecutionContext ctx, CancellationToken ct)
    {
        var snapshotChildren = group.Children.Select(DeepCloneNode).ToList();
        var callerSessionId = !string.IsNullOrEmpty(ctx.SessionId) ? ctx.SessionId : null;

        var session = _sessionManager.BeginSession(
            watchItemTag, $"Group:{group.Tag}",
            new Dictionary<string, string>(ctx.Parameters, StringComparer.OrdinalIgnoreCase),
            snapshotChildren,
            callerSessionId);

        ctx.SessionId = session.SessionId;
        Log("Session", $"Started {session.SessionId} for {watchItemTag}:Group:{group.Tag}");

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Cts.Token);

        try
        {
            var success = await ExecuteChildrenTrackedAsync(
                snapshotChildren, group.ExecutionType, group.FailAndContinue, ctx, session, linkedCts.Token);
            return success || group.FailAndContinue;
        }
        finally
        {
            _sessionManager.CompleteSession(session.SessionId);
            Log("Session", $"Completed {session.SessionId}: {session.SummaryText}");
        }
    }

    public async Task<bool> ExecuteSingleActionTrackedAsync(
        string watchItemTag, ActionConfig action, PipelineExecutionContext ctx, CancellationToken ct)
    {
        var clonedAction = (ActionConfig)DeepCloneNode(action);
        var callerSessionId = !string.IsNullOrEmpty(ctx.SessionId) ? ctx.SessionId : null;

        var session = _sessionManager.BeginSession(
            watchItemTag, $"Action:{action.ResolvedTag}",
            new Dictionary<string, string>(ctx.Parameters, StringComparer.OrdinalIgnoreCase),
            [clonedAction],
            callerSessionId);

        ctx.SessionId = session.SessionId;
        Log("Session", $"Started {session.SessionId} for {watchItemTag}:Action:{action.ResolvedTag}");

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Cts.Token);

        try
        {
            return await ExecuteActionTrackedAsync(clonedAction, ctx, session, linkedCts.Token);
        }
        finally
        {
            _sessionManager.CompleteSession(session.SessionId);
            Log("Session", $"Completed {session.SessionId}: {session.SummaryText}");
        }
    }

    public async Task RetryFailedAsync(ExecutionSession previousSession, CancellationToken ct)
    {
        var failedNodes = previousSession.FailedActions
            .Where(a => a.OriginalNode is not null)
            .Select(a => a.OriginalNode!)
            .ToList();

        if (failedNodes.Count == 0) return;

        var ctx = new PipelineExecutionContext
        {
            Parameters = new Dictionary<string, string>(
                previousSession.ResolvedParameters, StringComparer.OrdinalIgnoreCase)
        };

        var retrySession = _sessionManager.BeginSession(
            previousSession.WatchItemTag,
            $"Retry:{previousSession.EventType}",
            previousSession.ResolvedParameters,
            failedNodes);

        Log("Retry", $"Retrying {failedNodes.Count} failed action(s) for '{previousSession.WatchItemTag}'");

        try
        {
            await ExecuteChildrenTrackedAsync(
                failedNodes, ExecutionMode.Sequential, true, ctx, retrySession, ct);
        }
        finally
        {
            _sessionManager.CompleteSession(retrySession.SessionId);
            Log("Retry", $"Retry completed: {retrySession.SummaryText}");
        }
    }

    // ?? Core recursive executor (non-tracked) ?????????????????????????????

    protected async Task<bool> ExecuteChildrenAsync(
        List<IActionNode> children, ExecutionMode mode, bool parentFailAndContinue,
        PipelineExecutionContext ctx, CancellationToken ct)
    {
        if (mode == ExecutionMode.Parallel)
        {
            var tasks = children.Select(child =>
                ExecuteNodeAsync(child, ctx, ct)).ToList();
            var results = await Task.WhenAll(tasks);
            return results.All(r => r);
        }

        foreach (var child in children)
        {
            ct.ThrowIfCancellationRequested();
            var success = await ExecuteNodeAsync(child, ctx, ct);
            if (!success && !parentFailAndContinue)
            {
                Log("Pipeline", "Stopping � FailAndContinue=false");
                return false;
            }
        }
        return true;
    }

    protected async Task<bool> ExecuteNodeAsync(
        IActionNode node, PipelineExecutionContext ctx, CancellationToken ct)
    {
        OnNodeProgress(node, "Running");
        bool success;
        try
        {
            success = node switch
            {
                InitializeConfig init   => ExecuteInitialize(init, ctx),
                RefConfig refNode       => await ExecuteRefAsync(refNode, ctx, ct),
                ActionGroupConfig group => await ExecuteGroupAsync(group, ctx, ct),
                ActionConfig action     => await ExecuteActionAsync(action, ctx, ct),
                _                       => true,
            };
        }
        catch (OperationCanceledException)
        {
            OnNodeProgress(node, "Cancelled");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Node execution error");
            OnNodeFailed(node, -1, ex.Message);
            OnNodeProgress(node, "Failed");
            return false;
        }
        OnNodeProgress(node, success ? "Success" : "Failed");
        return success;
    }

    protected async Task<bool> ExecuteRefAsync(
        RefConfig refNode, PipelineExecutionContext ctx, CancellationToken ct)
    {
        if (!_templates.TryGetValue(refNode.TemplateID, out var template))
        {
            Log("Ref", $"Template '{refNode.TemplateID}' not found � skipping");
            return false;
        }

        Log("Ref", $"Expanding template: {refNode.TemplateID}");
        return await ExecuteChildrenAsync(template.Children, ExecutionMode.Sequential, true, ctx, ct);
    }

    // ?? Session-tracked recursive executor ????????????????????????????????

    protected async Task<bool> ExecuteChildrenTrackedAsync(
        List<IActionNode> children, ExecutionMode mode, bool parentFailAndContinue,
        PipelineExecutionContext ctx, ExecutionSession session, CancellationToken ct)
    {
        if (mode == ExecutionMode.Parallel)
        {
            var tasks = children.Select(child =>
                ExecuteNodeTrackedAsync(child, ctx, session, ct)).ToList();
            var results = await Task.WhenAll(tasks);
            return results.All(r => r);
        }

        foreach (var child in children)
        {
            ct.ThrowIfCancellationRequested();
            var success = await ExecuteNodeTrackedAsync(child, ctx, session, ct);
            if (!success && !parentFailAndContinue)
            {
                Log("Pipeline", "Stopping � FailAndContinue=false");
                return false;
            }
        }
        return true;
    }

    protected async Task<bool> ExecuteNodeTrackedAsync(
        IActionNode node, PipelineExecutionContext ctx,
        ExecutionSession session, CancellationToken ct)
    {
        OnNodeProgress(node, "Running");
        bool success;
        try
        {
            success = node switch
            {
                InitializeConfig init   => ExecuteInitialize(init, ctx),
                RefConfig refNode       => await ExecuteRefTrackedAsync(refNode, ctx, session, ct),
                ActionGroupConfig group => await ExecuteGroupTrackedAsync(group, ctx, session, ct),
                ActionConfig action     => await ExecuteActionTrackedAsync(action, ctx, session, ct),
                _                       => true,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Node execution error");
            success = false;
        }
        OnNodeProgress(node, success ? "Success" : "Failed");
        return success;
    }

    protected async Task<bool> ExecuteRefTrackedAsync(
        RefConfig refNode, PipelineExecutionContext ctx,
        ExecutionSession session, CancellationToken ct)
    {
        if (!_templates.TryGetValue(refNode.TemplateID, out var template))
        {
            Log("Ref", $"Template '{refNode.TemplateID}' not found � skipping");
            return false;
        }

        Log("Ref", $"Expanding template: {refNode.TemplateID}");
        return await ExecuteChildrenTrackedAsync(
            template.Children, ExecutionMode.Sequential, true, ctx, session, ct);
    }

    protected async Task<bool> ExecuteGroupTrackedAsync(
        ActionGroupConfig group, PipelineExecutionContext ctx,
        ExecutionSession session, CancellationToken ct)
    {
        Log("ActionGroup",
            $"[{group.Tag}] Mode={group.ExecutionType}, FailAndContinue={group.FailAndContinue}");

        var success = await ExecuteChildrenTrackedAsync(
            group.Children, group.ExecutionType, group.FailAndContinue, ctx, session, ct);

        Log("ActionGroup", $"[{group.Tag}] {(success ? "? Completed" : "? Failed")}");
        return success || group.FailAndContinue;
    }

    protected async Task<bool> ExecuteActionTrackedAsync(
        ActionConfig action, PipelineExecutionContext ctx,
        ExecutionSession session, CancellationToken ct)
    {
        var result = new ActionExecutionResult
        {
            ActionTag = action.ResolvedTag,
            ActionType = action.Type.ToString(),
            AgentName = action.AgentName,
            Command = action.Command,
            OriginalNode = action,
            StartedUtc = DateTime.UtcNow
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Publish 'Running' so the dashboard can flip the pill immediately
        // instead of waiting for the action to finish.
        _sessionManager.BeginAction(session.SessionId, result);
        try
        {
            var actionSuccess = await ExecuteActionAsync(action, ctx, ct);
            sw.Stop();
            result.Duration = sw.Elapsed;
            result.Outcome = actionSuccess ? ActionOutcome.Success : ActionOutcome.Failed;
        }
        catch (OperationCanceledException)
        {
            result.Outcome = ActionOutcome.Terminated;
            result.Duration = sw.Elapsed;
        }
        catch (TimeoutException)
        {
            result.Outcome = ActionOutcome.TimedOut;
            result.Duration = sw.Elapsed;
        }
        catch (Exception ex)
        {
            result.Outcome = ActionOutcome.Failed;
            result.ErrorMessage = ex.Message;
            result.Duration = sw.Elapsed;
        }

        _sessionManager.RecordResult(session.SessionId, result);
        session.TrackAgentAction(result);
        return !result.IsRetryable || action.FailAndContinue;
    }

    // ?? Initialize ?????????????????????????????????????????????????????

    protected virtual bool ExecuteInitialize(InitializeConfig init, PipelineExecutionContext ctx)
    {
        var path = ParameterResolver.Resolve(init.ParameterFile, ctx);
        Log("Initialize", $"Loading parameters from: {path}");
        ParameterResolver.LoadParameterFile(ctx, path);
        return true;
    }

    // ?? Host-specific dispatch ?????????????????????????????????????????

    /// <summary>
    /// Dispatches a single <see cref="ActionConfig"/> to the host's execution
    /// path. WPF includes smart retry + Local/Remote/SendMail; WebApi has a
    /// simpler Local/Remote path.
    /// </summary>
    protected abstract Task<bool> ExecuteActionAsync(
        ActionConfig action, PipelineExecutionContext ctx, CancellationToken ct);

    // ?? Event firing helpers (since events declared on base aren't directly
    //    invocable from derived classes) ?????????????????????????????????

    protected void OnLogEntry(PipelineLogEntry entry) => LogEntry?.Invoke(entry);
    protected void OnNodeProgress(IActionNode node, string status) => NodeProgress?.Invoke(node, status);
    protected void OnNodeFailed(IActionNode node, int exitCode, string error)
        => NodeFailed?.Invoke(node, exitCode, error);

    // ?? Logging helpers ????????????????????????????????????????????????

    protected void Log(string category, string message)
    {
        _logger.LogInformation("[{Category}] {Message}", category, message);
        OnLogEntry(new PipelineLogEntry(DateTime.Now, category, message));
    }

    protected void Log(string category, string message, PipelineExecutionContext ctx)
    {
        _logger.LogInformation("[{Category}] {Message}", category, message);
        OnLogEntry(new PipelineLogEntry(
            DateTime.Now, category, message,
            AgentName: null,
            SessionId: ctx.SessionId));
    }

    // ?? Deep clone for snapshot isolation ??????????????????????????????

    protected static IActionNode DeepCloneNode(IActionNode node) => node switch
    {
        ActionConfig a => new ActionConfig
        {
            Type = a.Type, AgentName = a.AgentName,
            Command = a.Command, Parameters = a.Parameters,
            Timeout = a.Timeout, PollInterval = a.PollInterval,
            FailAndContinue = a.FailAndContinue, IsReboot = a.IsReboot,
            Order = a.Order, Tag = a.Tag,
            UserName = a.UserName, Password = a.Password,
            From = a.From, To = a.To, Title = a.Title, Body = a.Body,
            Attachment = a.Attachment, Embed = a.Embed, LargeFilesShare = a.LargeFilesShare,
            MaxRetries = a.MaxRetries, RetryDelaySeconds = a.RetryDelaySeconds,
            RetryBackoff = a.RetryBackoff, RetryOnExitCodes = a.RetryOnExitCodes,
            CompletionCheckCommand = a.CompletionCheckCommand,
            CompletionPollIntervalSeconds = a.CompletionPollIntervalSeconds,
        },
        ActionGroupConfig g => new ActionGroupConfig
        {
            Tag = g.Tag, ExecutionType = g.ExecutionType,
            FailAndContinue = g.FailAndContinue,
            Children = g.Children.Select(DeepCloneNode).ToList()
        },
        InitializeConfig i => new InitializeConfig { Tag = i.Tag, ParameterFile = i.ParameterFile },
        RefConfig r => new RefConfig { TemplateID = r.TemplateID },
        _ => node
    };
}
