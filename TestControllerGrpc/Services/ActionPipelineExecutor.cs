using System.Diagnostics;
using System.IO;
using System.Net.Mail;
using System.Net.Mime;
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
public sealed class ActionPipelineExecutor : IActionPipelineExecutor
{
    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly ExecutionSessionManager _sessionManager;
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
        IAgentGrpcDispatcher dispatcher,
        ExecutionSessionManager sessionManager,
        ILogger<ActionPipelineExecutor> logger)
    {
        _dispatcher = dispatcher;
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

    /// <summary>Executes an ActionGroup's children (public for direct execution from UI).</summary>
    public async Task<bool> ExecuteGroupAsync(
        ActionGroupConfig group, PipelineExecutionContext ctx, CancellationToken ct)
    {
        Log("ActionGroup", $"[{group.Tag}] Mode={group.ExecutionType}, FailAndContinue={group.FailAndContinue}");

        var success = await ExecuteChildrenAsync(
            group.Children, group.ExecutionType, group.FailAndContinue, ctx, ct);

        Log("ActionGroup", $"[{group.Tag}] {(success ? "✓ Completed" : "✗ Failed")}");
        return success || group.FailAndContinue;
    }

    // ── Single Action ──────────────────────────────────────────────────

    /// <summary>Executes a single Action node (public for direct execution from UI).</summary>
    public async Task<bool> ExecuteSingleActionAsync(
        ActionConfig action, PipelineExecutionContext ctx, CancellationToken ct)
    {
        NodeProgress?.Invoke(action, "Running");
        var success = await ExecuteActionAsync(action, ctx, ct);
        NodeProgress?.Invoke(action, success ? "Success" : "Failed");
        return success;
    }

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

    // ── SendMail ───────────────────────────────────────────────────────

    /// <summary>
    /// Sends a notification email via SMTP with support for:
    ///   • Comma-separated attachment paths → copied to LargeFilesShare and linked in body
    ///   • Comma-separated embed file paths → inlined as HTML body content
    ///   • Files exceeding 500 KB are also redirected to LargeFilesShare as links
    /// </summary>
    private ActionResult ExecuteSendMail(ActionConfig resolved)
    {
        if (string.IsNullOrWhiteSpace(resolved.From) || string.IsNullOrWhiteSpace(resolved.To))
            return new ActionResult(false, -1, "SendMail: From and To addresses are required.");

        try
        {
            using var smtpClient = new SmtpClient("smtp")
            {
                UseDefaultCredentials = true
            };

            using var message = new MailMessage(resolved.From, resolved.To);
            message.Subject = resolved.Title;

            var body = resolved.Body ?? "";
            int linkCount = 0;
            bool isHtml = false;

            // ── Attachments → copy to LargeFilesShare and add links ────
            if (!string.IsNullOrWhiteSpace(resolved.Attachment))
            {
                body += Environment.NewLine + "---LINKS---:";

                foreach (var att in resolved.Attachment.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!File.Exists(att))
                    {
                        _logger.LogWarning("SendMail: Attachment file not found: {Path}", att);
                        Log("SendMail", $"⚠ Attachment file not found: {att}");
                        body += Environment.NewLine + $"Attachment file not found: {att}";
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(resolved.LargeFilesShare))
                    {
                        var accessibleLocation = Path.Combine(resolved.LargeFilesShare, Path.GetFileName(att));
                        try
                        {
                            File.Copy(att, accessibleLocation, overwrite: true);
                            body += Environment.NewLine + $"Link {linkCount++}: {accessibleLocation}";
                            Log("SendMail", $"Linked attachment: {accessibleLocation}");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "SendMail: Failed to copy attachment to share");
                            body += Environment.NewLine + $"Failed to copy attachment: {att} — {ex.Message}";
                        }
                    }
                    else
                    {
                        // No LargeFilesShare configured — attach directly
                        message.Attachments.Add(new Attachment(att));
                        Log("SendMail", $"Attached: {att}");
                    }
                }
            }

            // ── Embed files → inline content into body ─────────────────
            if (!string.IsNullOrWhiteSpace(resolved.Embed))
            {
                var embeddedBody = "";
                isHtml = true;

                foreach (var em in resolved.Embed.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!File.Exists(em))
                    {
                        _logger.LogWarning("SendMail: File to be embedded not found: {Path}", em);
                        Log("SendMail", $"⚠ Embed file not found: {em}");
                        embeddedBody += Environment.NewLine + $"File to be embedded not found: {em}";
                        continue;
                    }

                    var fileInfo = new FileInfo(em);

                    // Files over 500 KB → redirect to LargeFilesShare as link
                    if (fileInfo.Length > 524_288 && !string.IsNullOrWhiteSpace(resolved.LargeFilesShare))
                    {
                        var accessibleLocation = Path.Combine(resolved.LargeFilesShare, Path.GetFileName(em));
                        try
                        {
                            File.Copy(em, accessibleLocation, overwrite: true);
                            embeddedBody += Environment.NewLine + $"Link {linkCount++}: {accessibleLocation}";
                            Log("SendMail", $"Large embed redirected to link: {accessibleLocation}");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "SendMail: Failed to copy embed to share");
                            embeddedBody += Environment.NewLine + $"Failed to copy embed: {em} — {ex.Message}";
                        }
                        continue;
                    }

                    embeddedBody += Environment.NewLine + File.ReadAllText(em);
                    Log("SendMail", $"Embedded: {em} ({fileInfo.Length:N0} bytes)");
                }

                // Merge embedded content into the body as HTML
                if (!string.IsNullOrWhiteSpace(embeddedBody))
                {
                    var wrappedBody = $"<br /><div style='white-space:pre-wrap'>{body}</div>";

                    // If the embedded file is HTML with a </body> tag, inject before it
                    if (embeddedBody.Contains("</body>", StringComparison.OrdinalIgnoreCase))
                    {
                        body = embeddedBody
                            .Replace("</Body>", $"{wrappedBody}</Body>")
                            .Replace("</body>", $"{wrappedBody}</body>");
                    }
                    else
                    {
                        // Plain embedded content — wrap everything in HTML
                        body = $"<html><body>{embeddedBody}<br /><div style='white-space:pre-wrap'>{body}</div></body></html>";
                    }

                    message.AlternateViews.Add(
                        AlternateView.CreateAlternateViewFromString(body, null, MediaTypeNames.Text.Html));
                }
            }

            // ── Finalize body ──────────────────────────────────────────
            message.IsBodyHtml = isHtml;
            message.Body = isHtml
                ? body.Replace(Environment.NewLine, "<br />")
                : body;

            // ── Send ───────────────────────────────────────────────────
            smtpClient.Send(message);

            Log("SendMail", $"✓ Sent to {resolved.To} | Subject: {resolved.Title}");
            _logger.LogInformation("SendMail sent: From={From}, To={To}, Subject={Subject}",
                resolved.From, resolved.To, resolved.Title);

            return new ActionResult(true, 0, "");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendMail failed: From={From}, To={To}", resolved.From, resolved.To);
            Log("SendMail", $"✗ Failed: {ex.Message}");
            return new ActionResult(false, -1, $"SendMail failed: {ex.Message}");
        }
    }

    // ── Logging ────────────────────────────────────────────────────────

    private void Log(string category, string message)
    {
        _logger.LogInformation("[{Category}] {Message}", category, message);
        LogEntry?.Invoke(new PipelineLogEntry(DateTime.Now, category, message));
    }

    // ═══════════════════════════════════════════════════════════════════
    // SESSION-TRACKED EXECUTION (snapshot isolation + per-action results)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Executes an Event's children with session tracking and snapshot isolation.
    /// The live config is cloned before execution so hot-reloads don't affect a running pipeline.
    /// </summary>
    public async Task ExecuteEventTrackedAsync(
        string watchItemTag, EventConfig evt, PipelineExecutionContext ctx, CancellationToken ct)
    {
        // Snapshot isolation: clone the action tree so hot-reloads don't mutate in-flight nodes
        var snapshotChildren = evt.Children.Select(DeepCloneNode).ToList();

        var session = _sessionManager.BeginSession(
            watchItemTag, evt.Type,
            new Dictionary<string, string>(ctx.Parameters, StringComparer.OrdinalIgnoreCase),
            snapshotChildren);

        Log("Session", $"Started {session.SessionId} for {watchItemTag}:{evt.Type}");

        try
        {
            await ExecuteChildrenTrackedAsync(
                snapshotChildren, evt.ExecutionType, true, ctx, session, ct);
        }
        finally
        {
            _sessionManager.CompleteSession(session.SessionId);
            Log("Session", $"Completed {session.SessionId}: {session.SummaryText}");
        }
    }

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

        // Sequential
        foreach (var child in children)
        {
            ct.ThrowIfCancellationRequested();
            var success = await ExecuteNodeTrackedAsync(child, ctx, session, ct);
            if (!success && !parentFailAndContinue)
            {
                Log("Pipeline", "Stopping — FailAndContinue=false");
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

    private async Task<bool> ExecuteRefTrackedAsync(
        RefConfig refNode, PipelineExecutionContext ctx,
        ExecutionSession session, CancellationToken ct)
    {
        if (!_templates.TryGetValue(refNode.TemplateID, out var template))
        {
            Log("Ref", $"Template '{refNode.TemplateID}' not found — skipping");
            return false;
        }

        Log("Ref", $"Expanding template: {refNode.TemplateID}");
        return await ExecuteChildrenTrackedAsync(
            template.Children, ExecutionMode.Sequential, true, ctx, session, ct);
    }

    private async Task<bool> ExecuteGroupTrackedAsync(
        ActionGroupConfig group, PipelineExecutionContext ctx,
        ExecutionSession session, CancellationToken ct)
    {
        Log("ActionGroup", $"[{group.Tag}] Mode={group.ExecutionType}, FailAndContinue={group.FailAndContinue}");

        var success = await ExecuteChildrenTrackedAsync(
            group.Children, group.ExecutionType, group.FailAndContinue, ctx, session, ct);

        Log("ActionGroup", $"[{group.Tag}] {(success ? "✓ Completed" : "✗ Failed")}");
        return success || group.FailAndContinue;
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

    // ── Retry only failed actions from a previous session ──────────────

    /// <summary>
    /// Re-executes only the actions that failed in a previous session,
    /// using the same resolved parameters from the original run.
    /// </summary>
    public async Task RetryFailedAsync(ExecutionSession previousSession, CancellationToken ct)
    {
        var failedNodes = previousSession.FailedActions
            .Where(a => a.OriginalNode is not null)
            .Select(a => a.OriginalNode!)
            .ToList();

        if (failedNodes.Count == 0) return;

        // Reconstruct context from the original session
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

    // ── Deep clone for snapshot isolation ───────────────────────────────

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
