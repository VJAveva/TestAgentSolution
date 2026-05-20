using System.IO;
using System.Net.Mail;
using System.Net.Mime;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// WPF host pipeline executor. After the Phase 2.16 spike this class is a
/// thin host shell: orchestration (sequential/parallel children, session
/// tracking, snapshot isolation, retry-failed, Initialize/Ref handling,
/// event firing) lives in <see cref="PipelineExecutorBase"/>. Host-specific
/// responsibilities kept here:
/// <list type="bullet">
///   <item>Smart-retry dispatch via <see cref="IAgentGrpcDispatcher"/>.</item>
///   <item>SendMail (SMTP, attachments, embedded HTML, LargeFilesShare links).</item>
///   <item><c>[ResultsEmail]</c> token expansion via the WPF-only TRX parsing
///     and HTML report generator.</item>
/// </list>
/// </summary>
public sealed class ActionPipelineExecutor : PipelineExecutorBase
{
    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly TrxResultsParser _parser;
    private readonly BuildResultsAggregator _aggregator;
    private readonly BuildReportHtmlGenerator _htmlGenerator;

    public ActionPipelineExecutor(
        IAgentGrpcDispatcher dispatcher,
        ExecutionSessionManager sessionManager,
        ILogger<ActionPipelineExecutor> logger,
        TrxResultsParser parser,
        BuildResultsAggregator aggregator,
        BuildReportHtmlGenerator htmlGenerator)
        : base(sessionManager, logger)
    {
        _dispatcher = dispatcher;
        _parser = parser;
        _aggregator = aggregator;
        _htmlGenerator = htmlGenerator;
    }

    // ?? Action execution with smart retry ??????????????????????????????

    protected override async Task<bool> ExecuteActionAsync(
        ActionConfig action, PipelineExecutionContext ctx, CancellationToken ct)
    {
        var resolved = ParameterResolver.ResolveAction(action, ctx);
        int maxAttempts = 1 + Math.Max(0, action.MaxRetries);
        int delaySeconds = Math.Max(1, action.RetryDelaySeconds > 0 ? action.RetryDelaySeconds : 10);
        bool isExponential = !string.Equals(action.RetryBackoff, "Fixed", StringComparison.OrdinalIgnoreCase);
        var retryExitCodes = ParseRetryExitCodes(action.RetryOnExitCodes);

        ActionResult result = new ActionResult(false, -1, "Not executed");

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (ct.IsCancellationRequested) break;

            if (attempt > 1)
            {
                int currentDelay = isExponential
                    ? delaySeconds * (int)Math.Pow(2, attempt - 2)
                    : delaySeconds;

                // Cap at 5 minutes max
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
                        Log("Action", $"RunRemoteCommand \u2192 {resolved.AgentName}: {resolved.Command} {resolved.Parameters}");
                    else
                        Log("Retry", $"RunRemoteCommand \u2192 {resolved.AgentName} (attempt {attempt})");
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
                    result = ExecuteSendMail(resolved, ctx);
                    break;

                default:
                    result = new ActionResult(false, -1, $"Unknown type: {action.Type}");
                    break;
            }

            if (result.Success)
            {
                if (attempt > 1)
                    Log("Retry", $"\u2713 Succeeded on attempt {attempt} of {maxAttempts}");
                else
                    Log("Action", $"\u2713 Success (exit={result.ExitCode})");
                return true;
            }

            // Check if we should retry this specific failure
            if (attempt < maxAttempts && ShouldRetry(result, retryExitCodes))
            {
                var agentCtx = string.IsNullOrEmpty(resolved.AgentName) ? "Controller" : resolved.AgentName;
                Log("Action", $"\u2717 Failed on {agentCtx} (exit={result.ExitCode}): {result.ErrorMessage} \u2014 will retry");
                continue;
            }

            break;
        }

        // All attempts exhausted
        {
            var agentInfo = string.IsNullOrEmpty(resolved.AgentName) ? "Controller" : resolved.AgentName;
            var cmdInfo = $"{resolved.Command} {resolved.Parameters}".Trim();
            if (cmdInfo.Length > 120) cmdInfo = cmdInfo[..120] + "\u2026";

            if (maxAttempts > 1)
                Log("Action", $"\u2717 FAILED on {agentInfo} after {maxAttempts} attempts: {cmdInfo}");
            else
                Log("Action", $"\u2717 FAILED on {agentInfo}: {cmdInfo}");

            Log("Action", $"  Exit code: {result.ExitCode}");
            Log("Action", $"  Error: {result.ErrorMessage}");
        }

        OnNodeFailed(action, result.ExitCode, result.ErrorMessage);
        return action.FailAndContinue;
    }

    /// <summary>Determines if a failed result should be retried based on exit code filters.</summary>
    private static bool ShouldRetry(ActionResult result, HashSet<int>? retryExitCodes)
    {
        if (retryExitCodes == null || retryExitCodes.Count == 0)
            return true;
        return retryExitCodes.Contains(result.ExitCode);
    }

    /// <summary>Parses comma-separated exit codes. Returns null if empty (= retry all).</summary>
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

    // ?? SendMail ???????????????????????????????????????????????????????

    /// <summary>
    /// Sends a notification email via SMTP with support for:
    ///   � Comma-separated attachment paths ? copied to LargeFilesShare and linked in body
    ///   � Comma-separated embed file paths ? inlined as HTML body content
    ///   � Files exceeding 500 KB are also redirected to LargeFilesShare as links
    ///   � [ResultsEmail] token in Body ? auto-generates HTML email from parsed .trx results
    /// </summary>
    private ActionResult ExecuteSendMail(ActionConfig resolved, PipelineExecutionContext ctx)
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

            // ?? [ResultsEmail] token ? auto-generate HTML email from .trx results ??
            if (body.Contains("[ResultsEmail]"))
            {
                try
                {
                    var resultsPath = ctx.Parameters.GetValueOrDefault("_ResultsPath")
                        ?? ctx.Parameters.GetValueOrDefault("ResultsPath")
                        ?? "";
                    if (string.IsNullOrWhiteSpace(resultsPath))
                        resultsPath = resolved.Parameters; // fallback: use Parameters field as results path

                    if (!string.IsNullOrWhiteSpace(resultsPath) && Directory.Exists(resultsPath))
                    {
                        var buildNode = _parser.ParseBuildFolder(resultsPath);
                        buildNode = _aggregator.EvaluateBuildHealth(buildNode);

                        var product = ctx.Parameters.GetValueOrDefault("_Product")
                            ?? ctx.Parameters.GetValueOrDefault("Product")
                            ?? "AVEVA System Platform";
                        var machine = Environment.MachineName;
                        var buildPath = resultsPath;
                        var reportLink = ctx.Parameters.GetValueOrDefault("_ReportLink")
                            ?? ctx.Parameters.GetValueOrDefault("ReportLink")
                            ?? "";

                        body = _htmlGenerator.GenerateEmailHtml(buildNode, product, machine, buildPath, reportLink);
                        isHtml = true;
                        Log("SendMail", $"Generated results email from: {resultsPath} ({buildNode.TotalTests} tests, {buildNode.PassRate:F1}% pass rate)");
                    }
                    else
                    {
                        Log("SendMail", $"\u26A0 [ResultsEmail] token found but results path not found or empty: '{resultsPath}'");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SendMail: Failed to generate results email");
                    Log("SendMail", $"\u26A0 Failed to generate results email: {ex.Message} \u2014 falling back to plain body");
                }
            }

            // ?? Attachments ? copy to LargeFilesShare and add links ????
            if (!string.IsNullOrWhiteSpace(resolved.Attachment))
            {
                body += Environment.NewLine + "---LINKS---:";

                foreach (var att in resolved.Attachment.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!File.Exists(att))
                    {
                        _logger.LogWarning("SendMail: Attachment file not found: {Path}", att);
                        Log("SendMail", $"\u26A0 Attachment file not found: {att}");
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
                            body += Environment.NewLine + $"Failed to copy attachment: {att} � {ex.Message}";
                        }
                    }
                    else
                    {
                        // No LargeFilesShare configured � attach directly
                        message.Attachments.Add(new Attachment(att));
                        Log("SendMail", $"Attached: {att}");
                    }
                }
            }

            // ?? Embed files ? inline content into body ?????????????????
            if (!string.IsNullOrWhiteSpace(resolved.Embed))
            {
                var embeddedBody = "";
                isHtml = true;

                foreach (var em in resolved.Embed.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!File.Exists(em))
                    {
                        _logger.LogWarning("SendMail: File to be embedded not found: {Path}", em);
                        Log("SendMail", $"? Embed file not found: {em}");
                        embeddedBody += Environment.NewLine + $"File to be embedded not found: {em}";
                        continue;
                    }

                    var fileInfo = new FileInfo(em);

                    // Files over 500 KB ? redirect to LargeFilesShare as link
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
                            embeddedBody += Environment.NewLine + $"Failed to copy embed: {em} � {ex.Message}";
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
                        // Plain embedded content � wrap everything in HTML
                        body = $"<html><body>{embeddedBody}<br /><div style='white-space:pre-wrap'>{body}</div></body></html>";
                    }

                    message.AlternateViews.Add(
                        AlternateView.CreateAlternateViewFromString(body, null, MediaTypeNames.Text.Html));
                }
            }

            // ?? Finalize body ??????????????????????????????????????????
            message.IsBodyHtml = isHtml;
            message.Body = isHtml
                ? body.Replace(Environment.NewLine, "<br />")
                : body;

            // ?? Send ???????????????????????????????????????????????????
            smtpClient.Send(message);

            Log("SendMail", $"? Sent to {resolved.To} | Subject: {resolved.Title}");
            _logger.LogInformation("SendMail sent: From={From}, To={To}, Subject={Subject}",
                resolved.From, resolved.To, resolved.Title);

            return new ActionResult(true, 0, "");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendMail failed: From={From}, To={To}", resolved.From, resolved.To);
            Log("SendMail", $"? Failed: {ex.Message}");
            return new ActionResult(false, -1, $"SendMail failed: {ex.Message}");
        }
    }
}
