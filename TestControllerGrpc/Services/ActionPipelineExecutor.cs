using Microsoft.Extensions.Logging;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Core execution engine for the WatchList action pipeline.
///
/// Walks the action tree depth-first:
///   - ActionGroup (Sequential): runs children one-by-one
///   - ActionGroup (Parallel):   runs children concurrently via Task.WhenAll
///   - Action (RunCommand):      executes locally on controller
///   - Action (RunRemoteCommand): dispatches to agent via gRPC
///   - Action (SendMail):        sends notification email
///   - Initialize:               loads parameter file into ExecutionContext
///   - Ref:                      expands to Template children inline
///
/// Respects FailAndContinue: if false, a failure stops the group.
/// </summary>
public sealed class ActionPipelineExecutor
{
    private readonly AgentGrpcDispatcher _dispatcher;
    private readonly ILogger<ActionPipelineExecutor> _logger;
    private Dictionary<string, TemplateConfig> _templates = new();

    /// <summary>Raised for every action/event in the pipeline.</summary>
    public event Action<PipelineLogEntry>? LogEntry;

    /// <summary>
    /// Raised when an IActionNode starts or finishes execution.
    /// Status: "Running", "Success", "Failed".
    /// </summary>
    public event Action<IActionNode, string>? NodeProgress;

    public ActionPipelineExecutor(
        AgentGrpcDispatcher dispatcher,
        ILogger<ActionPipelineExecutor> logger)
    {
        _dispatcher = dispatcher;
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

    /// <summary>
    /// Executes an Event's children (the top-level entry point).
    /// </summary>
    public async Task ExecuteEventAsync(
        EventConfig evt, PipelineExecutionContext ctx, CancellationToken ct)
    {
        Log("Event", $"Triggered: Type={evt.Type}, Exec={evt.ExecutionType}");
        await ExecuteChildrenAsync(evt.Children, evt.ExecutionType, true, ctx, ct);
        Log("Event", "Completed");
    }

    // ── Core recursive executor ────────────────────────────────────────

    private async Task<bool> ExecuteChildrenAsync(
        List<IActionNode> children,
        ExecutionMode mode,
        bool parentFailAndContinue,
        PipelineExecutionContext ctx,
        CancellationToken ct)
    {
        if (mode == ExecutionMode.Parallel)
        {
            var tasks = children.Select(child =>
                ExecuteNodeAsync(child, ctx, ct)).ToList();
            var results = await Task.WhenAll(tasks);
            return results.All(r => r);
        }
        else // Sequential
        {
            foreach (var child in children)
            {
                ct.ThrowIfCancellationRequested();
                var success = await ExecuteNodeAsync(child, ctx, ct);
                if (!success && !parentFailAndContinue)
                {
                    Log("Pipeline", "Stopping — FailAndContinue=false");
                    return false;
                }
            }
            return true;
        }
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Node execution error");
            success = false;
        }
        NodeProgress?.Invoke(node, success ? "Success" : "Failed");
        return success;
    }

    // ── Initialize ─────────────────────────────────────────────────────

    private bool ExecuteInitialize(InitializeConfig init, PipelineExecutionContext ctx)
    {
        var path = ParameterResolver.Resolve(init.ParameterFile, ctx);
        Log("Initialize", $"Loading parameters from: {path}");
        ParameterResolver.LoadParameterFile(ctx, path);
        return true;
    }

    // ── Ref → Template expansion ───────────────────────────────────────

    private async Task<bool> ExecuteRefAsync(
        RefConfig refNode, PipelineExecutionContext ctx, CancellationToken ct)
    {
        if (!_templates.TryGetValue(refNode.TemplateID, out var template))
        {
            Log("Ref", $"Template '{refNode.TemplateID}' not found — skipping");
            return false;
        }

        Log("Ref", $"Expanding template: {refNode.TemplateID}");
        return await ExecuteChildrenAsync(template.Children, ExecutionMode.Sequential, true, ctx, ct);
    }

    // ── ActionGroup ────────────────────────────────────────────────────

    private async Task<bool> ExecuteGroupAsync(
        ActionGroupConfig group, PipelineExecutionContext ctx, CancellationToken ct)
    {
        Log("ActionGroup", $"[{group.Tag}] Mode={group.ExecutionType}, FailAndContinue={group.FailAndContinue}");

        var success = await ExecuteChildrenAsync(
            group.Children, group.ExecutionType, group.FailAndContinue, ctx, ct);

        Log("ActionGroup", $"[{group.Tag}] {(success ? "✓ Completed" : "✗ Failed")}");
        return success || group.FailAndContinue;
    }

    // ── Single Action ──────────────────────────────────────────────────

    private async Task<bool> ExecuteActionAsync(
        ActionConfig action, PipelineExecutionContext ctx, CancellationToken ct)
    {
        var resolved = ParameterResolver.ResolveAction(action, ctx);
        ActionResult result;

        switch (action.Type)
        {
            case ActionType.RunRemoteCommand:
                Log("Action", $"RunRemoteCommand → {resolved.AgentName}: {resolved.Command} {resolved.Parameters}");
                result = await _dispatcher.ExecuteRemoteCommandAsync(action, ctx, ct);
                break;

            case ActionType.RunCommand:
                Log("Action", $"RunCommand (local): {resolved.Command} {resolved.Parameters}");
                result = await _dispatcher.ExecuteLocalCommandAsync(action, ctx, ct);
                break;

            case ActionType.SendMail:
                Log("Action", $"SendMail: To={resolved.To}, Title={resolved.Title}");
                result = ExecuteSendMail(resolved);
                break;

            default:
                result = new ActionResult(false, -1, $"Unknown type: {action.Type}");
                break;
        }

        if (!result.Success)
            Log("Action", $"✗ Failed: {result.ErrorMessage} (exit={result.ExitCode})");
        else
            Log("Action", $"✓ Success (exit={result.ExitCode})");

        return result.Success || action.FailAndContinue;
    }

    // ── SendMail stub ──────────────────────────────────────────────────

    private ActionResult ExecuteSendMail(ActionConfig resolved)
    {
        // In production, implement SmtpClient-based mail sending here.
        // For now, log the intent.
        _logger.LogInformation("SendMail: From={From}, To={To}, Title={Title}",
            resolved.From, resolved.To, resolved.Title);
        Log("SendMail", $"To: {resolved.To} | Subject: {resolved.Title}");
        return new ActionResult(true, 0, "");
    }

    // ── Logging ────────────────────────────────────────────────────────

    private void Log(string category, string message)
    {
        _logger.LogInformation("[{Category}] {Message}", category, message);
        LogEntry?.Invoke(new PipelineLogEntry(DateTime.Now, category, message));
    }
}

public sealed record PipelineLogEntry(DateTime Timestamp, string Category, string Message);
