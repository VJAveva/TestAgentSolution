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
    /// The third argument is the owning pipeline tag - see <see cref="IActionPipelineExecutor"/>.
    /// </summary>
    public event Action<IActionNode, string, string?>? NodeProgress;

    /// <summary>
    /// Raised when an action node fails, providing exit code and error details.
    /// The fourth argument is the owning pipeline tag - see <see cref="IActionPipelineExecutor"/>.
    /// </summary>
    public event Action<IActionNode, int, string, string?>? NodeFailed;

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
    /// The pipeline whose run the CURRENT async flow belongs to, stamped onto every NodeProgress.
    /// AsyncLocal rather than a field: one executor instance serves concurrent runs, and each run's
    /// continuations must carry their own scope.
    /// </summary>
    private readonly AsyncLocal<string?> _pipelineScope = new();

    /// <summary>Scopes everything this async flow raises to <paramref name="watchItemTag"/>.</summary>
    private void EnterPipelineScope(string? watchItemTag)
        => _pipelineScope.Value = string.IsNullOrWhiteSpace(watchItemTag) ? null : watchItemTag;

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
        EnterPipelineScope(ctx.WatchItemTag);
        Log("Event", $"Triggered: Type={evt.Type}, Exec={evt.ExecutionType}");
        await ExecuteChildrenAsync(evt.Children, evt.ExecutionType, true, ctx, ct);
        Log("Event", ctx.FatalError is null ? "Completed" : $"ABORTED - {ctx.FatalError}");
    }

    public async Task<bool> ExecuteGroupAsync(
        ActionGroupConfig group, PipelineExecutionContext ctx, CancellationToken ct)
    {
        if (BlockedBySkip(group, $"ActionGroup '{group.Tag}'"))
        {
            MarkSubtreeSkipped(group, null);
            return true;
        }

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
        if (BlockedBySkip(action, $"Action '{action.ResolvedTag}'"))
        {
            OnNodeProgress(action, "Skipped");
            return true;
        }

        OnNodeProgress(action, "Running");
        bool success;
        try
        {
            success = await DispatchActionAsync(action, ctx, ct);
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
        EnterPipelineScope(watchItemTag);

        // Checked before the session exists so a skipped event does not open a session or take a lock.
        if (BlockedBySkip(evt, $"Event '{evt.Type}'"))
        {
            foreach (IActionNode child in evt.Children) MarkSubtreeSkipped(child, null);
            return;
        }

        // Snapshot isolation: clone the action tree so hot-reloads don't mutate in-flight nodes.
        var snapshotChildren = evt.Children.Select(DeepCloneNode).ToList();
        // Use the caller's sessionId if provided (e.g. from WebApi controller).
        var callerSessionId = !string.IsNullOrEmpty(ctx.SessionId) ? ctx.SessionId : null;

        var session = _sessionManager.BeginSession(
            watchItemTag, evt.Type,
            new Dictionary<string, string>(ctx.Parameters, StringComparer.OrdinalIgnoreCase),
            snapshotChildren,
            callerSessionId,
            _templates);

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
            if (ctx.FatalError is not null)
                Log("Session", $"ABORTED {session.SessionId}: {ctx.FatalError}");
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
        EnterPipelineScope(watchItemTag);

        if (BlockedBySkip(group, $"ActionGroup '{group.Tag}'"))
        {
            MarkSubtreeSkipped(group, null);
            return true;
        }

        var snapshotChildren = group.Children.Select(DeepCloneNode).ToList();
        var callerSessionId = !string.IsNullOrEmpty(ctx.SessionId) ? ctx.SessionId : null;

        var session = _sessionManager.BeginSession(
            watchItemTag, $"Group:{group.Tag}",
            new Dictionary<string, string>(ctx.Parameters, StringComparer.OrdinalIgnoreCase),
            snapshotChildren,
            callerSessionId,
            _templates);

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
        EnterPipelineScope(watchItemTag);

        // "Execute Action" on a skipped row reaches here directly, bypassing the per-node gate.
        if (BlockedBySkip(action, $"Action '{action.ResolvedTag}'"))
        {
            OnNodeProgress(action, "Skipped");
            return true;
        }

        var clonedAction = (ActionConfig)DeepCloneNode(action);
        var callerSessionId = !string.IsNullOrEmpty(ctx.SessionId) ? ctx.SessionId : null;

        var session = _sessionManager.BeginSession(
            watchItemTag, $"Action:{action.ResolvedTag}",
            new Dictionary<string, string>(ctx.Parameters, StringComparer.OrdinalIgnoreCase),
            [clonedAction],
            callerSessionId,
            _templates);

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
            failedNodes,
            sessionId: null,
            templates: _templates);

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

    /// <summary>
    /// How many children one group starts at once. The process-wide bound on actions actually in
    /// flight is <see cref="PipelineConcurrency"/> - this only limits task fan-out within a group.
    /// </summary>
    private static int MaxParallelDegree => PipelineConcurrency.MaxConcurrentActions;

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
                try
                {
                    if (ctx.FatalError is not null) return false;
                    return await ExecuteNodeAsync(child, ctx, ct);
                }
                finally { gate.Release(); }
            }).ToList();
            var results = await Task.WhenAll(tasks);
            return results.All(r => r);
        }

        foreach (var child in children)
        {
            ct.ThrowIfCancellationRequested();
            if (ctx.FatalError is not null) return false;

            var success = await ExecuteNodeAsync(child, ctx, ct);
            if (success) continue;

            // A fatal parameter failure overrides FailAndContinue: continuing would dispatch actions
            // whose tokens cannot resolve.
            if (ctx.FatalError is not null) return false;

            if (!parentFailAndContinue)
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
        // The untracked path must gate too, or whether a node is skipped would depend on which entry
        // point the caller happened to use.
        if (node is ISkippableNode skippable &&
            !SkipGate.Evaluate(skippable, SkipState.NotSkipped).ShouldRun)
        {
            Log("Skip", DescribeSkip(node, skippable));
            MarkSubtreeSkipped(node, null);
            return true;
        }

        OnNodeProgress(node, "Running");
        bool success;
        try
        {
            success = node switch
            {
                InitializeConfig init   => ExecuteInitialize(init, ctx),
                RefConfig refNode       => await ExecuteRefAsync(refNode, ctx, ct),
                ActionGroupConfig group => await ExecuteGroupAsync(group, ctx, ct),
                ActionConfig action     => await DispatchActionAsync(action, ctx, ct),
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
        PipelineExecutionContext ctx, ExecutionSession session, CancellationToken ct,
        string groupPath = "")
    {
        if (mode == ExecutionMode.Parallel)
        {
            // Scale fix: Bound parallelism to prevent ThreadPool starvation at 200 agents.
            using var gate = new SemaphoreSlim(MaxParallelDegree, MaxParallelDegree);
            var tasks = children.Select(async child =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    if (ctx.FatalError is not null) return false;
                    return await ExecuteNodeTrackedAsync(child, ctx, session, ct, groupPath);
                }
                finally { gate.Release(); }
            }).ToList();
            var results = await Task.WhenAll(tasks);
            return results.All(r => r);
        }

        foreach (var child in children)
        {
            ct.ThrowIfCancellationRequested();
            if (ctx.FatalError is not null) return false;

            var success = await ExecuteNodeTrackedAsync(child, ctx, session, ct, groupPath);
            if (success) continue;

            // A fatal parameter failure overrides FailAndContinue: continuing would dispatch actions
            // whose tokens cannot resolve.
            if (ctx.FatalError is not null) return false;

            if (!parentFailAndContinue)
            {
                Log("Pipeline", "Stopping � FailAndContinue=false");
                return false;
            }
        }
        return true;
    }

    /// <summary>Stateless, so a shared instance avoids threading a dependency through three executors.</summary>
    private static readonly IExecutionGate SkipGate = new ExecutionGate();

    protected async Task<bool> ExecuteNodeTrackedAsync(
        IActionNode node, PipelineExecutionContext ctx,
        ExecutionSession session, CancellationToken ct, string groupPath = "")
    {
        // A skipped subtree is never entered, so by construction nothing here can inherit a skip; the gate
        // still owns the rule so the executor and the pre-run manifest can never disagree.
        if (node is ISkippableNode skippable &&
            !SkipGate.Evaluate(skippable, SkipState.NotSkipped).ShouldRun)
        {
            Log("Skip", DescribeSkip(node, skippable));
            MarkSubtreeSkipped(node, session);
            // Skipped is not failure: returning false would abort the sequence under FailAndContinue=false.
            return true;
        }

        OnNodeProgress(node, "Running");
        PublishContainerProgress(node, session, "Running");
        bool success;
        try
        {
            success = node switch
            {
                InitializeConfig init   => ExecuteInitialize(init, ctx),
                RefConfig refNode       => await ExecuteRefTrackedAsync(refNode, ctx, session, ct, groupPath),
                ActionGroupConfig group => await ExecuteGroupTrackedAsync(group, ctx, session, ct, groupPath),
                ActionConfig action     => await ExecuteActionTrackedAsync(action, ctx, session, ct, groupPath),
                _                       => true,
            };
        }
        catch (OperationCanceledException)
        {
            OnNodeProgress(node, "Cancelled");
            PublishContainerProgress(node, session, "Cancelled");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Node execution error");
            OnNodeFailed(node, -1, ex.Message);
            OnNodeProgress(node, "Failed");
            PublishContainerProgress(node, session, "Failed");
            return false;
        }
        OnNodeProgress(node, success ? "Success" : "Failed");
        PublishContainerProgress(node, session, success ? "Success" : "Failed");
        return success;
    }

    /// <summary>One log line per skipped node, naming the node the user has to un-skip.</summary>
    private static string DescribeSkip(IActionNode node, ISkippableNode skippable)
    {
        var label = node switch
        {
            ActionGroupConfig g => $"ActionGroup '{g.Tag}'",
            ActionConfig a      => $"Action '{(!string.IsNullOrWhiteSpace(a.Tag) ? a.Tag : a.Command)}'",
            RefConfig r         => $"Ref '{r.TemplateID}'",
            _                   => node.GetType().Name,
        };
        return $"{label} skipped \u2014 {ReasonOf(skippable)}";
    }

    private static string ReasonOf(ISkippableNode node)
        => string.IsNullOrWhiteSpace(node.SkipReason) ? "no reason given" : node.SkipReason!;

    /// <summary>
    /// Entry-point guard. The per-node gate only ever sees CHILDREN, so a public call that targets a skipped
    /// node directly - "Execute Action" on a skipped row, or executing a skipped group or event - would run it.
    /// </summary>
    private bool BlockedBySkip(ISkippableNode node, string label)
    {
        if (SkipGate.Evaluate(node, SkipState.NotSkipped).ShouldRun) return false;
        Log("Skip", $"{label} skipped \u2014 {ReasonOf(node)}");
        return true;
    }

    /// <summary>
    /// Descendants of a skipped group never reach the gate, so their status is stamped here instead -
    /// otherwise they would sit at Idle for the whole run and read as "never reached".
    /// </summary>
    private void MarkSubtreeSkipped(IActionNode node, ExecutionSession? session)
    {
        OnNodeProgress(node, "Skipped");
        if (session is not null) PublishContainerProgress(node, session, "Skipped");

        if (node is ActionGroupConfig group)
            foreach (IActionNode child in group.Children)
                MarkSubtreeSkipped(child, session);
    }

    /// <summary>
    /// Emits session-aware progress for the CONTAINER nodes an action's own result does not cover.
    /// </summary>
    /// <remarks>
    /// Actions already report through <c>ExecutionSessionManager.BeginAction/RecordResult</c>, which
    /// stamp the SessionId. Groups and Refs did not, so their only broadcast came from the legacy
    /// executor event with no SessionId — and the dashboard routes per-node messages BY SessionId,
    /// so group status could never appear there.
    /// </remarks>
    private void PublishContainerProgress(IActionNode node, ExecutionSession session, string status)
    {
        var tag = node switch
        {
            ActionGroupConfig group => group.Tag,
            RefConfig refNode       => refNode.TemplateID,
            _                       => null,
        };
        if (string.IsNullOrEmpty(tag)) return;

        _sessionManager.PublishNodeProgress(session.SessionId, tag, node.NodeType, status);
    }

    protected async Task<bool> ExecuteRefTrackedAsync(
        RefConfig refNode, PipelineExecutionContext ctx,
        ExecutionSession session, CancellationToken ct, string groupPath = "")
    {
        if (!_templates.TryGetValue(refNode.TemplateID, out var template))
        {
            Log("Ref", $"Template '{refNode.TemplateID}' not found � skipping");
            return false;
        }

        Log("Ref", $"Expanding template: {refNode.TemplateID}");
        return await ExecuteChildrenTrackedAsync(
            template.Children, ExecutionMode.Sequential, true, ctx, session, ct, groupPath);
    }

    protected async Task<bool> ExecuteGroupTrackedAsync(
        ActionGroupConfig group, PipelineExecutionContext ctx,
        ExecutionSession session, CancellationToken ct, string groupPath = "")
    {
        Log("ActionGroup",
            $"[{group.Tag}] Mode={group.ExecutionType}, FailAndContinue={group.FailAndContinue}");

        var childPath = string.IsNullOrEmpty(groupPath)
            ? group.Tag
            : groupPath + ActionExecutionResult.GroupSeparator + group.Tag;

        var success = await ExecuteChildrenTrackedAsync(
            group.Children, group.ExecutionType, group.FailAndContinue, ctx, session, ct, childPath);

        Log("ActionGroup", $"[{group.Tag}] {(success ? "? Completed" : "? Failed")}");
        return success || group.FailAndContinue;
    }

    protected async Task<bool> ExecuteActionTrackedAsync(
        ActionConfig action, PipelineExecutionContext ctx,
        ExecutionSession session, CancellationToken ct, string groupPath = "")
    {
        var result = new ActionExecutionResult
        {
            ActionTag = action.ResolvedTag,
            ActionType = action.Type.ToString(),
            AgentName = action.AgentName,
            Command = action.Command,
            GroupPath = groupPath,
            OriginalNode = action,
            StartedUtc = DateTime.UtcNow
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Publish 'Running' so the dashboard can flip the pill immediately
        // instead of waiting for the action to finish.
        _sessionManager.BeginAction(session.SessionId, result);
        try
        {
            // An unresolved token means a parameter source did not load. Running anyway sends the
            // literal text (e.g. "[_Agent2]") to a shell, which fails far from the real cause.
            var unresolved = ParameterResolver.FindUnresolvedTokens(action, ctx);
            if (unresolved.Count > 0)
            {
                var tokens = string.Join(", ", unresolved.Select(t => "[" + t + "]"));
                result.Outcome = ActionOutcome.Failed;
                result.ErrorMessage = ctx.IsStandaloneTemplateRun
                    ? $"Unresolved parameter(s) {tokens} - this template has no settings of its own"
                      + " - run it from a pipeline that uses it."
                    : $"Unresolved parameter(s) {tokens}"
                      + " - the Initialize parameter source for this pipeline did not load.";
                Log("Action", result.ErrorMessage);
            }
            else
            {
                var actionSuccess = await DispatchActionAsync(action, ctx, ct);
                result.Outcome = actionSuccess ? ActionOutcome.Success : ActionOutcome.Failed;
            }

            sw.Stop();
            result.Duration = sw.Elapsed;
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

        var failure = ParameterResolver.TryLoadInitializeSource(ctx, path, init.Profile, ctx.WatchItemTag);
        if (failure is null) return true;

        var label = string.IsNullOrWhiteSpace(init.Tag) ? "Initialize" : $"Initialize '{init.Tag}'";
        var message =
            $"{label} could not load its parameters - {failure} Stopping the run before any action: "
            + "every [Token] that source supplies would otherwise be sent on unresolved.";

        ctx.FatalError = message;
        Log("Initialize", message);
        OnNodeFailed(init, -1, message);
        return false;
    }

    // ?? Host-specific dispatch ?????????????????????????????????????????

    /// <summary>
    /// Dispatches a single <see cref="ActionConfig"/> to the host's execution
    /// path. WPF includes smart retry + Local/Remote/SendMail; WebApi has a
    /// simpler Local/Remote path.
    /// </summary>
    protected abstract Task<bool> ExecuteActionAsync(
        ActionConfig action, PipelineExecutionContext ctx, CancellationToken ct);

    /// <summary>
    /// The ONE seam every action passes through, tracked and untracked alike, so the process-wide
    /// dispatch cap cannot be bypassed by picking a different entry point.
    /// </summary>
    private async Task<bool> DispatchActionAsync(
        ActionConfig action, PipelineExecutionContext ctx, CancellationToken ct)
    {
        using var permit = await PipelineConcurrency.AcquireAsync(ct);
        return await ExecuteActionAsync(action, ctx, ct);
    }

    // ?? Event firing helpers (since events declared on base aren't directly
    //    invocable from derived classes) ?????????????????????????????????

    protected void OnLogEntry(PipelineLogEntry entry) => LogEntry?.Invoke(entry);
    protected void  OnNodeProgress(IActionNode node, string status)
        => NodeProgress?.Invoke(node, status, _pipelineScope.Value);
    protected void OnNodeFailed(IActionNode node, int exitCode, string error)
        => NodeFailed?.Invoke(node, exitCode, error, _pipelineScope.Value);

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

    /// <summary>
    /// Snapshot copy of a node, so a hot-reload cannot mutate an in-flight tree.
    /// </summary>
    protected static IActionNode DeepCloneNode(IActionNode node) => node.DeepClone();
}
