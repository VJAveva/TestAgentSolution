using Microsoft.Extensions.Logging;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Services;

/// <summary>
/// Standalone WebApi pipeline executor. After the Phase 2.16 spike this
/// class is a thin host shell: orchestration (sequential/parallel children,
/// session tracking, retry loop, snapshot isolation, Initialize/Ref handling,
/// event firing) lives in <see cref="PipelineExecutorBase"/>. The only
/// host-specific responsibility is dispatching a single action via
/// <see cref="IAgentGrpcDispatcher"/> with smart retry.
/// </summary>
public sealed class StandalonePipelineExecutor : PipelineExecutorBase
{
    private readonly IAgentGrpcDispatcher _dispatcher;

    public StandalonePipelineExecutor(
        IAgentGrpcDispatcher dispatcher,
        ExecutionSessionManager sessionManager,
        ILogger<StandalonePipelineExecutor> logger,
        TestControllerGrpc.Locking.ILockRegistry? lockRegistry = null)
        : base(sessionManager, logger, lockRegistry)
    {
        _dispatcher = dispatcher;
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
                        Log("Action", $"RunRemoteCommand \u2192 {resolved.AgentName}: {SecurityRedactor.RedactCommandLine(resolved.Command, resolved.Parameters)}");
                    else
                        Log("Retry", $"RunRemoteCommand \u2192 {resolved.AgentName} (attempt {attempt})");
                    result = await _dispatcher.ExecuteRemoteCommandAsync(action, ctx, ct);
                    break;

                case ActionType.RunCommand:
                    if (attempt == 1)
                        Log("Action", $"RunCommand (local): {SecurityRedactor.RedactCommandLine(resolved.Command, resolved.Parameters)}");
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
                    Log("Retry", $"\u2713 Succeeded on attempt {attempt} of {maxAttempts}");
                else
                    Log("Action", $"\u2713 Success (exit={result.ExitCode})");
                return true;
            }

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
            var cmdInfo = SecurityRedactor.RedactCommandLine(resolved.Command, resolved.Parameters).Trim();
            if (cmdInfo.Length > 120) cmdInfo = cmdInfo[..120] + "\u2026";

            // Coalesce failure header + exit code + error into ONE structured
            // entry (single queryable line) with agent/action as distinct fields.
            var attemptsNote = maxAttempts > 1 ? $" after {maxAttempts} attempts" : "";
            var err = SecurityRedactor.Redact(result.ErrorMessage) ?? string.Empty;
            Log("Action",
                $"\u2717 FAILED on {agentInfo}{attemptsNote}: {cmdInfo} (exit {result.ExitCode}): {err}",
                ctx,
                severity: "Error",
                agentName: agentInfo,
                action: string.IsNullOrEmpty(resolved.Tag) ? null : resolved.Tag,
                exception: err);
        }

        OnNodeFailed(action, result.ExitCode, SecurityRedactor.Redact(result.ErrorMessage) ?? string.Empty);
        return action.FailAndContinue;
    }

    // ?? Retry helpers (host-local; could be lifted to base in a later slice) ??

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
}
