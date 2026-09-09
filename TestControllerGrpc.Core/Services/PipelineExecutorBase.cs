using System.Threading;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Locking;
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

    /// <summary>
    /// The single-run pipeline lock authority. Non-null only in the WPF controller host
    /// (the standalone WebApi forwards lock ops to the controller and passes null here).
    /// When present, each tracked run releases its lock in <c>finally</c> after teardown.
    /// </summary>
    protected readonly ILockRegistry? _lockRegistry;
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
        ILogger logger,
        ILockRegistry? lockRegistry = null)
    {
        _sessionManager = sessionManager;
        _logger = logger;
        _lockRegistry = lockRegistry;
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

    /// <summary>
    /// Captures this run's single-run lock token from the context. The trigger path that
    /// acquired the lock threads the per-acquisition token via <see cref="PipelineExecutionContext.LockToken"/>.
    /// Returns empty when no token was threaded (standalone WebApi, or hosts that own the
    /// release themselves such as the WPF view model) — in which case the executor never
    /// releases the lock.
    /// </summary>
    private string CaptureLockToken(string watchItemTag, PipelineExecutionContext ctx)
    {
        _ = watchItemTag;
        return ctx.LockToken;
    }

    /// <summary>
    /// Copies the triggering user's attribution from the context onto the session so every
    /// surface can show "by &lt;user&gt;". Only fills fields the trigger path left unset, so a
    /// host that already stamped the session (e.g. the WebApi <c>ExecutionController</c>) wins.
    /// </summary>
    private static void ApplyOwnerAttribution(ExecutionSession session, PipelineExecutionContext ctx)
    {
        if (string.IsNullOrEmpty(session.UserId) && !string.IsNullOrEmpty(ctx.UserId))
            session.UserId = ctx.UserId;
        if (string.IsNullOrEmpty(session.UserDisplayName) && !string.IsNullOrEmpty(ctx.UserDisplayName))
            session.UserDisplayName = ctx.UserDisplayName;
        if (string.IsNullOrEmpty(session.UserRole) && !string.IsNullOrEmpty(ctx.UserRole))
            session.UserRole = ctx.UserRole;
    }

    /// <summary>
    /// Releases this run's single-run lock. No-op when there is no authority, no token, or
    /// the token is stale (a newer acquisition reused the pipeline) — so a torn-down run can
    /// never free someone else's lock.
    /// </summary>
    private void ReleaseRunLock(string watchItemTag, string lockToken)
    {
        if (_lockRegistry is null || string.IsNullOrEmpty(lockToken))
            return;
        if (_lockRegistry.TryRelease(watchItemTag, lockToken))
            Log("Lock", $"Released single-run lock for {watchItemTag}");
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

        ApplyOwnerAttribution(session, ctx);

        ctx.SessionId = session.SessionId;

        // Every trigger path funnels through here, so the JSON config's per-pipeline layer resolves
        // without each call site having to remember to set the tag. Set only when the caller did not.
        if (string.IsNullOrEmpty(ctx.WatchItemTag))
            ctx.WatchItemTag = watchItemTag;

        Log("Session", $"Started {session.SessionId} for {watchItemTag}:{evt.Type}");

        // Capture the single-run lock token now so we release exactly THIS run's lock
        // (a later acquisition that reused the pipeline would carry a different token).
        var lockToken = CaptureLockToken(watchItemTag, ctx);

        // Renew the lock for the run's duration so the expiry sweeper never reaps it
        // mid-run; disposed (with linkedCts) after the run unwinds.
        using var lockRenewal = new LockRenewalTimer(
            _lockRegistry, watchItemTag, lockToken, _lockRegistry?.RenewalInterval ?? TimeSpan.Zero);

        // Link the external cancellation token with the session's own CTS so
        // both _sessionManager.CancelSession() and external cancellation work.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, session.CancellationToken);

        try
        {
            await ExecuteChildrenTrackedAsync(
                snapshotChildren, evt.ExecutionType, true, ctx, session, linkedCts.Token);
        }
        finally
        {
            // RELEASE only after the run has fully stopped (completed or cancelled + torn
            // down). On cancel, control reaches here only once the awaited pipeline unwinds.
            _sessionManager.CompleteSession(session.SessionId);
            ReleaseRunLock(watchItemTag, lockToken);
            Log("Session", $"Completed {session.SessionId}: {session.SummaryText}");
        }
    }

    public Task ExecuteTemplateTrackedAsync(
        string pipelineTag, TemplateConfig template, PipelineExecutionContext ctx, CancellationToken ct)
    {
        // A template is a named, standalone list of action nodes; run it as a synthetic sequential Event so it
        // reuses the tracked event pipeline (session, snapshot isolation, lock lifecycle) with no duplication.
        var synthetic = new EventConfig
        {
            Type = $"Template:{template.ID}",
            ExecutionType = ExecutionMode.Sequential,
            Children = template.Children,
        };
        return ExecuteEventTrackedAsync(pipelineTag, synthetic, ctx, ct);
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

        ApplyOwnerAttribution(session, ctx);

        ctx.SessionId = session.SessionId;
        Log("Session", $"Started {session.SessionId} for {watchItemTag}:Group:{group.Tag}");

        var lockToken = CaptureLockToken(watchItemTag, ctx);

        using var lockRenewal = new LockRenewalTimer(
            _lockRegistry, watchItemTag, lockToken, _lockRegistry?.RenewalInterval ?? TimeSpan.Zero);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, session.CancellationToken);

        try
        {
            var success = await ExecuteChildrenTrackedAsync(
                snapshotChildren, group.ExecutionType, group.FailAndContinue, ctx, session, linkedCts.Token);
            return success || group.FailAndContinue;
        }
        finally
        {
            _sessionManager.CompleteSession(session.SessionId);
            ReleaseRunLock(watchItemTag, lockToken);
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

        ApplyOwnerAttribution(session, ctx);

        ctx.SessionId = session.SessionId;
        Log("Session", $"Started {session.SessionId} for {watchItemTag}:Action:{action.ResolvedTag}");

        var lockToken = CaptureLockToken(watchItemTag, ctx);

        using var lockRenewal = new LockRenewalTimer(
            _lockRegistry, watchItemTag, lockToken, _lockRegistry?.RenewalInterval ?? TimeSpan.Zero);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, session.CancellationToken);

        try
        {
            return await ExecuteActionTrackedAsync(clonedAction, ctx, session, linkedCts.Token);
        }
        finally
        {
            _sessionManager.CompleteSession(session.SessionId);
            ReleaseRunLock(watchItemTag, lockToken);
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

    /// <summary>Max concurrent agent operations in parallel mode to prevent ThreadPool starvation.</summary>
    private const int MaxParallelDegree = 50;

    protected async Task<bool> ExecuteChildrenAsync(
        List<IActionNode> children, ExecutionMode mode, bool parentFailAndContinue,
        PipelineExecutionContext ctx, CancellationToken ct)
    {
        if (mode == ExecutionMode.Parallel)
        {
            // Scale fix: Bound parallelism to prevent ThreadPool starvation at 200 agents.
            // Without this, 200 parallel Task.WhenAll calls exhaust all available threads.
            using var gate = new SemaphoreSlim(MaxParallelDegree, MaxParallelDegree);
            var tasks = children.Select(async child =>
            {
                await gate.WaitAsync(ct);
                try { return await ExecuteNodeAsync(child, ctx, ct); }
                finally { gate.Release(); }
            }).ToList();
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
            // Scale fix: Bound parallelism to prevent ThreadPool starvation at 200 agents.
            using var gate = new SemaphoreSlim(MaxParallelDegree, MaxParallelDegree);
            var tasks = children.Select(async child =>
            {
                await gate.WaitAsync(ct);
                try { return await ExecuteNodeTrackedAsync(child, ctx, session, ct); }
                finally { gate.Release(); }
            }).ToList();
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
            _sessionManager.RecordResult(session.SessionId, result);
            session.TrackAgentAction(result);
            throw; // Re-throw so callers can update tree nodes to "Cancelled"
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

        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            ParameterResolver.LoadJsonConfig(ctx, path, init.Profile, ctx.WatchItemTag);
        else
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
    protected void  OnNodeProgress(IActionNode node, string status) => NodeProgress?.Invoke(node, status);
    protected void OnNodeFailed(IActionNode node, int exitCode, string error)
        => NodeFailed?.Invoke(node, exitCode, error);

    // ?? Logging helpers ????????????????????????????????????????????????

    protected void Log(string category, string message)
    {
        var redactedMessage = SecurityRedactor.Redact(message) ?? string.Empty;
        _logger.LogInformation("[{Category}] {Message}", category, redactedMessage);
        OnLogEntry(new PipelineLogEntry(DateTime.Now, category, redactedMessage));
    }

    protected void Log(string category, string message, PipelineExecutionContext ctx)
    {
        var redactedMessage = SecurityRedactor.Redact(message) ?? string.Empty;
        _logger.LogInformation("[{Category}] {Message}", category, redactedMessage);
        OnLogEntry(new PipelineLogEntry(
            DateTime.Now, category, redactedMessage,
            AgentName: null,
            SessionId: ctx.SessionId,
            RunId: ctx.SessionId));
    }

    /// <summary>
    /// Structured log helper. Populates the distinct tracing fields
    /// (<paramref name="agentName"/>, <paramref name="action"/>,
    /// <paramref name="exception"/>) and the run id (from
    /// <see cref="PipelineExecutionContext.SessionId"/>) so a failure becomes a
    /// single queryable entry instead of several fragmented lines, and so the
    /// component vs agent can be filtered independently.
    /// </summary>
    protected void Log(string category, string message, PipelineExecutionContext ctx,
        string? severity, string? agentName = null, string? action = null,
        string? pipeline = null, string? exception = null)
    {
        var redactedMessage = SecurityRedactor.Redact(message) ?? string.Empty;
        _logger.LogInformation("[{Category}] {Message}", category, redactedMessage);
        OnLogEntry(new PipelineLogEntry(
            DateTime.Now, category, redactedMessage,
            AgentName: agentName,
            SessionId: ctx.SessionId,
            Severity: severity,
            RunId: ctx.SessionId,
            Pipeline: pipeline,
            Action: action,
            Exception: SecurityRedactor.Redact(exception)));
    }

    // ?? Deep clone for snapshot isolation ??????????????????????????????

    protected static IActionNode DeepCloneNode(IActionNode node) => node switch
    {
        ActionConfig a => new ActionConfig
        {
            NodeId = a.NodeId,
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
            NodeId = g.NodeId,
            Tag = g.Tag, ExecutionType = g.ExecutionType,
            FailAndContinue = g.FailAndContinue,
            Children = g.Children.Select(DeepCloneNode).ToList()
        },
        InitializeConfig i => new InitializeConfig { NodeId = i.NodeId, Tag = i.Tag, ParameterFile = i.ParameterFile },
        RefConfig r => new RefConfig { NodeId = r.NodeId, TemplateID = r.TemplateID },
        _ => node
    };
}
