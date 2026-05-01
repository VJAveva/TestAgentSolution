using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Services;

/// <summary>
/// Full pipeline executor for standalone WebApi deployment.
/// Walks the action tree depth-first, executing ActionGroups, Actions,
/// Initialize, and Ref nodes � mirroring the WPF ActionPipelineExecutor
/// without any WPF/UI dependencies.
///
/// Delegates actual command execution to <see cref="IAgentGrpcDispatcher"/>
/// (remote gRPC and local process execution).
/// </summary>
public sealed class StandalonePipelineExecutor : IActionPipelineExecutor
{
    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly ExecutionSessionManager _sessionManager;
    private readonly ILogger<StandalonePipelineExecutor> _logger;
    private Dictionary<string, TemplateConfig> _templates = new(StringComparer.OrdinalIgnoreCase);

    public event Action<PipelineLogEntry>? LogEntry;
    public event Action<IActionNode, string>? NodeProgress;
    public event Action<IActionNode, int, string>? NodeFailed;

    public StandalonePipelineExecutor(
        IAgentGrpcDispatcher dispatcher,
        ExecutionSessionManager sessionManager,
        ILogger<StandalonePipelineExecutor> logger)
    {
        _dispatcher = dispatcher;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public void LoadTemplates(IEnumerable<TemplateConfig> templates)
    {
        _templates = templates.ToDictionary(t => t.ID, StringComparer.OrdinalIgnoreCase);
        _logger.LogInformation("Loaded {Count} templates", _templates.Count);
    }

    // ?? Top-level entry points ?????????????????????????????????????????

    public async Task ExecuteEventAsync(EventConfig ev, PipelineExecutionContext ctx, CancellationToken ct)
    {
        Log("Event", $"Triggered: Type={ev.Type}, Exec={ev.ExecutionType}");
        await ExecuteChildrenAsync(ev.Children, ev.ExecutionType, true, ctx, ct);
        Log("Event", "Completed");
    }

    public async Task<bool> ExecuteGroupAsync(ActionGroupConfig group, PipelineExecutionContext ctx, CancellationToken ct)
    {
        Log("ActionGroup", $"[{group.Tag}] Mode={group.ExecutionType}, FailAndContinue={group.FailAndContinue}");
        var success = await ExecuteChildrenAsync(group.Children, group.ExecutionType, group.FailAndContinue, ctx, ct);
        Log("ActionGroup", $"[{group.Tag}] {(success ? "? Completed" : "? Failed")}");
        return success || group.FailAndContinue;
    }

    public async Task<bool> ExecuteSingleActionAsync(ActionConfig action, PipelineExecutionContext ctx, CancellationToken ct)
    {
        NodeProgress?.Invoke(action, "Running");
        bool success;
        try
        {
            success = await ExecuteActionAsync(action, ctx, ct);
        }
        catch (OperationCanceledException)
        {
            NodeProgress?.Invoke(action, "Cancelled");
            return false;
        }
        NodeProgress?.Invoke(action, success ? "Success" : "Failed");
        return success;
    }

    // ?? Session-tracked execution ??????????????????????????????????????

    public async Task ExecuteEventTrackedAsync(
        string watchItemTag, EventConfig evt, PipelineExecutionContext ctx, CancellationToken ct)
    {
        var snapshotChildren = evt.Children.Select(DeepCloneNode).ToList();
        var callerSessionId = !string.IsNullOrEmpty(ctx.SessionId) ? ctx.SessionId : null;

        var session = _sessionManager.BeginSession(
            watchItemTag, evt.Type,
            new Dictionary<string, string>(ctx.Parameters, StringComparer.OrdinalIgnoreCase),
            snapshotChildren,
            callerSessionId);

        ctx.SessionId = session.SessionId;
        PipelineExecutionContext.AmbientSessionId.Value = session.SessionId;
        Log("Session", $"Started {session.SessionId} for {watchItemTag}:{evt.Type}");

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Cts.Token);

        try
        {
            await ExecuteChildrenTrackedAsync(
                snapshotChildren, evt.ExecutionType, true, ctx, session, linkedCts.Token);
        }
        finally
        {
            PipelineExecutionContext.AmbientSessionId.Value = null;
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

    // ?? Core recursive executor (non-tracked) ??????????????????????????

    private async Task<bool> ExecuteChildrenAsync(
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

    private async Task<bool> ExecuteNodeAsync(
        IActionNode node, PipelineExecutionContext ctx, CancellationToken ct)
    {
        NodeProgress?.Invoke(node, "Running");
        bool success;
        try
        {
            success = node switch
            {
                InitializeConfig init => ExecuteInitialize(init, ctx),
                RefConfig refNode => await ExecuteRefAsync(refNode, ctx, ct),
                ActionGroupConfig group => await ExecuteGroupAsync(group, ctx, ct),
                ActionConfig action => await ExecuteActionAsync(action, ctx, ct),
                _ => true,
            };
        }
        catch (OperationCanceledException)
        {
            NodeProgress?.Invoke(node, "Cancelled");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Node execution error");
            NodeFailed?.Invoke(node, -1, ex.Message);
            NodeProgress?.Invoke(node, "Failed");
            return false;
        }
        NodeProgress?.Invoke(node, success ? "Success" : "Failed");
        return success;
    }

    // ?? Session-tracked recursive executor ?????????????????????????????

    private async Task<bool> ExecuteChildrenTrackedAsync(
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

    private async Task<bool> ExecuteNodeTrackedAsync(
        IActionNode node, PipelineExecutionContext ctx,
        ExecutionSession session, CancellationToken ct)
    {
        NodeProgress?.Invoke(node, "Running");
        bool success;
        try
        {
            success = node switch
            {
                InitializeConfig init => ExecuteInitialize(init, ctx),
                RefConfig refNode => await ExecuteRefTrackedAsync(refNode, ctx, session, ct),
                ActionGroupConfig group => await ExecuteGroupTrackedAsync(group, ctx, session, ct),
                ActionConfig action => await ExecuteActionTrackedAsync(action, ctx, session, ct),
                _ => true,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Node execution error");
            success = false;
        }
        NodeProgress?.Invoke(node, success ? "Success" : "Failed");
        return success;
    }

    private async Task<bool> ExecuteGroupTrackedAsync(
        ActionGroupConfig group, PipelineExecutionContext ctx,
        ExecutionSession session, CancellationToken ct)
    {
        Log("ActionGroup", $"[{group.Tag}] Mode={group.ExecutionType}, FailAndContinue={group.FailAndContinue}");
        var success = await ExecuteChildrenTrackedAsync(
            group.Children, group.ExecutionType, group.FailAndContinue, ctx, session, ct);
        Log("ActionGroup", $"[{group.Tag}] {(success ? "? Completed" : "? Failed")}");
        return success || group.FailAndContinue;
    }

    private async Task<bool> ExecuteRefTrackedAsync(
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

    private async Task<bool> ExecuteActionTrackedAsync(
        ActionConfig action, PipelineExecutionContext ctx,
        ExecutionSession session, CancellationToken ct)
    {
        var result = new ActionExecutionResult
        {
            ActionTag = !string.IsNullOrWhiteSpace(action.Order) ? action.Order : action.Command,
            ActionType = action.Type.ToString(),
            AgentName = action.AgentName,
            Command = action.Command,
            OriginalNode = action,
            StartedUtc = DateTime.UtcNow
        };

        var sw = Stopwatch.StartNew();
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
        return !result.IsRetryable || action.FailAndContinue;
    }

    // ?? Action execution with smart retry ??????????????????????????????

    private async Task<bool> ExecuteActionAsync(
        ActionConfig action, PipelineExecutionContext ctx, CancellationToken ct)
    {
        var resolved = ParameterResolver.ResolveAction(action, ctx);
        int maxAttempts = 1 + Math.Max(0, action.MaxRetries);
        int delaySeconds = Math.Max(1, action.RetryDelaySeconds > 0 ? action.RetryDelaySeconds : 10);
        bool isExponential = !string.Equals(action.RetryBackoff, "Fixed", StringComparison.OrdinalIgnoreCase);
        var retryExitCodes = ParseRetryExitCodes(action.RetryOnExitCodes);

        ActionResult result = new(false, -1, "Not executed");

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (ct.IsCancellationRequested) break;

            if (attempt > 1)
            {
                int currentDelay = isExponential
                    ? delaySeconds * (int)Math.Pow(2, attempt - 2)
                    : delaySeconds;
                currentDelay = Math.Min(currentDelay, 300);

                Log("Retry", $"Attempt {attempt}/{maxAttempts} in {currentDelay}s " +
                    $"(backoff={action.RetryBackoff}, last exit={result.ExitCode})");

                try { await Task.Delay(currentDelay * 1000, ct); }
                catch (TaskCanceledException) { break; }
            }

            switch (action.Type)
            {
                case ActionType.RunRemoteCommand:
                    if (attempt == 1)
                        Log("Action", $"RunRemoteCommand ? {resolved.AgentName}: {resolved.Command} {resolved.Parameters}");
                    else
                        Log("Retry", $"RunRemoteCommand ? {resolved.AgentName} (attempt {attempt})");
                    result = await _dispatcher.ExecuteRemoteCommandAsync(action, ctx, ct);
                    break;

                case ActionType.RunCommand:
                    if (attempt == 1)
                        Log("Action", $"RunCommand (local): {resolved.Command} {resolved.Parameters}");
                    else
                        Log("Retry", $"RunCommand (local) (attempt {attempt})");
                    result = await _dispatcher.ExecuteLocalCommandAsync(action, ctx, ct);
                    break;

                case ActionType.SendMail:
                    Log("Action", $"SendMail: To={resolved.To}, Title={resolved.Title}");
                    result = new ActionResult(true, 0, "SendMail not supported in standalone mode");
                    break;

                default:
                    result = new ActionResult(false, -1, $"Unknown type: {action.Type}");
                    break;
            }

            if (result.Success)
            {
                if (attempt > 1)
                    Log("Retry", $"? Succeeded on attempt {attempt} of {maxAttempts}");
                else
                    Log("Action", $"? Success (exit={result.ExitCode})");
                return true;
            }

            if (attempt < maxAttempts && ShouldRetry(result, retryExitCodes))
            {
                var agentCtx = string.IsNullOrEmpty(resolved.AgentName) ? "Controller" : resolved.AgentName;
                Log("Action", $"? Failed on {agentCtx} (exit={result.ExitCode}): {result.ErrorMessage} � will retry");
                continue;
            }

            break;
        }

        // All attempts exhausted
        {
            var agentInfo = string.IsNullOrEmpty(resolved.AgentName) ? "Controller" : resolved.AgentName;
            var cmdInfo = $"{resolved.Command} {resolved.Parameters}".Trim();
            if (cmdInfo.Length > 120) cmdInfo = cmdInfo[..120] + "�";

            if (maxAttempts > 1)
                Log("Action", $"? FAILED on {agentInfo} after {maxAttempts} attempts: {cmdInfo}");
            else
                Log("Action", $"? FAILED on {agentInfo}: {cmdInfo}");

            Log("Action", $"  Exit code: {result.ExitCode}");
            Log("Action", $"  Error: {result.ErrorMessage}");
        }

        NodeFailed?.Invoke(action, result.ExitCode, result.ErrorMessage);
        return action.FailAndContinue;
    }

    // ?? Initialize ?????????????????????????????????????????????????????

    private bool ExecuteInitialize(InitializeConfig init, PipelineExecutionContext ctx)
    {
        var path = ParameterResolver.Resolve(init.ParameterFile, ctx);
        Log("Initialize", $"Loading parameters from: {path}");
        ParameterResolver.LoadParameterFile(ctx, path);
        return true;
    }

    // ?? Ref ? Template expansion ???????????????????????????????????????

    private async Task<bool> ExecuteRefAsync(
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

    // ?? Helpers ????????????????????????????????????????????????????????

    private static bool ShouldRetry(ActionResult result, HashSet<int>? retryExitCodes)
    {
        if (retryExitCodes == null || retryExitCodes.Count == 0) return true;
        return retryExitCodes.Contains(result.ExitCode);
    }

    private static HashSet<int>? ParseRetryExitCodes(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        var codes = new HashSet<int>();
        foreach (var part in spec.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, out int code))
                codes.Add(code);
        }
        return codes.Count > 0 ? codes : null;
    }

    private void Log(string category, string message)
    {
        var sid = PipelineExecutionContext.AmbientSessionId.Value;
        var prefixed = !string.IsNullOrEmpty(sid) ? $"[{sid}] {message}" : message;
        _logger.LogInformation("[{Category}] {Message}", category, prefixed);
        LogEntry?.Invoke(new PipelineLogEntry(DateTime.Now, category, prefixed));
    }

    private static IActionNode DeepCloneNode(IActionNode node) => node switch
    {
        ActionConfig a => new ActionConfig
        {
            Type = a.Type, AgentName = a.AgentName,
            Command = a.Command, Parameters = a.Parameters,
            Timeout = a.Timeout, PollInterval = a.PollInterval,
            FailAndContinue = a.FailAndContinue, IsReboot = a.IsReboot,
            Order = a.Order, UserName = a.UserName, Password = a.Password,
            From = a.From, To = a.To, Title = a.Title, Body = a.Body,
            Attachment = a.Attachment, Embed = a.Embed, LargeFilesShare = a.LargeFilesShare,
            MaxRetries = a.MaxRetries, RetryDelaySeconds = a.RetryDelaySeconds,
            RetryBackoff = a.RetryBackoff, RetryOnExitCodes = a.RetryOnExitCodes,
            CompletionCheckCommand = a.CompletionCheckCommand,
            CompletionPollIntervalSeconds = a.CompletionPollIntervalSeconds,
            EnableInstallLog = a.EnableInstallLog,
            InstallLogPollSeconds = a.InstallLogPollSeconds,
            InstallLogRoot = a.InstallLogRoot,
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
