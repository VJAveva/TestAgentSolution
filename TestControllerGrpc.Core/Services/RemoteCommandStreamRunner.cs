using System.Diagnostics;
using Grpc.Core;
using TestAgentGrpc;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Result of a single streamed RunCommand round-trip with an agent.
/// The dispatcher (WPF / WebApi) wraps this with its own resilience,
/// health tracking, and exit-code classification policy.
/// </summary>
public readonly record struct RemoteCommandStreamResult(
    bool ReceivedCompleted,
    int ExitCode,
    string ErrorMessage,
    IReadOnlyList<string> StderrTail);

/// <summary>
/// Phase 2.15 spike: shared gRPC streaming logic factored out of
/// <c>AgentGrpcDispatcher</c> (WPF) and <c>StandaloneAgentDispatcher</c>
/// (WebApi). Owns the duplicated parts:
/// <list type="bullet">
///   <item>Building the <see cref="RunCommandRequest"/> DTO from a resolved <see cref="ActionConfig"/>.</item>
///   <item>Iterating <c>RunCommandStreamed</c> events and forwarding stdout/stderr/progress.</item>
///   <item>Capping the in-memory stderr ring at 20 lines (callers use the tail for error reports).</item>
///   <item>Synthesizing a default error message when the agent never reports completion.</item>
/// </list>
/// What this helper deliberately does <b>not</b> own:
/// <list type="bullet">
///   <item>Polly resilience policies (WPF only).</item>
///   <item>Circuit-breaker / consecutive-failure health tracking (WPF only).</item>
///   <item>Reboot-wait, agent-free-wait, exit-code classification (callers' policy).</item>
/// </list>
/// </summary>
public static class RemoteCommandStreamRunner
{
    /// <summary>Maximum number of stderr lines retained for the post-run error report.</summary>
    public const int StderrTailCapacity = 20;

    /// <summary>
    /// Streams a <c>RunCommand</c> RPC against <paramref name="client"/> and
    /// returns once the server stream completes or <paramref name="ct"/> is
    /// cancelled.
    /// </summary>
    /// <param name="client">A connected gRPC client for the target agent.</param>
    /// <param name="agentName">Logical name; used for callback context only.</param>
    /// <param name="resolved">Action with all <c>[Tokens]</c> already resolved.</param>
    /// <param name="ct">Cancellation token (typically already linked with the per-action timeout).</param>
    /// <param name="outputReceived">Optional callback for stdout/stderr/progress lines (agentName, line, kind).</param>
    /// <param name="onProgressTick">
    /// Optional callback fired roughly every <paramref name="progressInterval"/> while the
    /// stream is open, so callers (WPF) can emit "long-running" log lines.
    /// Receives (agentName, command, elapsed).
    /// </param>
    /// <param name="progressInterval">How often to fire <paramref name="onProgressTick"/>. Defaults to 5 minutes.</param>
    public static async Task<RemoteCommandStreamResult> StreamAsync(
        TestAgentService.TestAgentServiceClient client,
        string agentName,
        ActionConfig resolved,
        CancellationToken ct,
        Action<string, string, string>? outputReceived = null,
        Action<string, string, TimeSpan>? onProgressTick = null,
        TimeSpan? progressInterval = null)
    {
        var request = BuildRequest(resolved);

        using var call = client.RunCommandStreamed(request, cancellationToken: ct);

        int exitCode = 0;
        string errorMessage = "";
        var stderrLines = new List<string>(StderrTailCapacity);
        bool receivedCompleted = false;

        var startTimestamp = Stopwatch.GetTimestamp();
        var lastProgressLog = startTimestamp;
        var interval = progressInterval ?? TimeSpan.FromMinutes(5);

        await foreach (var evt in call.ResponseStream.ReadAllAsync(ct))
        {
            switch (evt.EventType)
            {
                case ExecutionEventType.EventStdoutLine:
                    outputReceived?.Invoke(agentName, SecurityRedactor.Redact(evt.OutputLine) ?? string.Empty, "stdout");
                    break;
                case ExecutionEventType.EventStderrLine:
                    var stderrLine = SecurityRedactor.Redact(evt.OutputLine) ?? string.Empty;
                    outputReceived?.Invoke(agentName, stderrLine, "stderr");
                    stderrLines.Add(stderrLine);
                    if (stderrLines.Count > StderrTailCapacity)
                        stderrLines.RemoveAt(0);
                    break;
                case ExecutionEventType.EventProgress:
                    outputReceived?.Invoke(agentName,
                        SecurityRedactor.Redact($"Progress: {evt.ProgressPct:F0}% \u2014 {evt.Detail}") ?? string.Empty, "info");
                    break;
                case ExecutionEventType.EventCompleted:
                    exitCode = evt.ExitCode;
                    receivedCompleted = true;
                    break;
                case ExecutionEventType.EventFailed:
                    errorMessage = SecurityRedactor.Redact(evt.ErrorMessage) ?? string.Empty;
                    exitCode = -1;
                    break;
            }

            if (onProgressTick is not null)
            {
                var now = Stopwatch.GetTimestamp();
                if (Stopwatch.GetElapsedTime(lastProgressLog, now) >= interval)
                {
                    onProgressTick(agentName, resolved.Command,
                        Stopwatch.GetElapsedTime(startTimestamp, now));
                    lastProgressLog = now;
                }
            }
        }

        // Synthesize a default error if the agent never reported completion.
        if (!receivedCompleted && string.IsNullOrEmpty(errorMessage))
        {
            exitCode = -1;
            errorMessage = $"Agent '{agentName}' did not report completion. " +
                "Process may have crashed, been killed, or gRPC connection was lost.";
        }

        // Synthesize a default error if exit was non-zero with no message.
        if (exitCode != 0 && string.IsNullOrEmpty(errorMessage))
        {
            errorMessage = stderrLines.Count > 0
                ? $"Process exited with code {exitCode}. Last stderr: " +
                  string.Join(" | ", stderrLines.TakeLast(5))
                : $"Process exited with code {exitCode}. No error output captured. " +
                  "Check if the process requires user interaction (security dialogs, UAC prompts).";
        }

        return new RemoteCommandStreamResult(
            receivedCompleted, exitCode, errorMessage, stderrLines);
    }

    /// <summary>Builds the gRPC request DTO from a resolved <see cref="ActionConfig"/>.</summary>
    public static RunCommandRequest BuildRequest(ActionConfig resolved) => new()
    {
        Command = resolved.Command,
        Arguments = resolved.Parameters,
        IsReboot = resolved.IsReboot,
        UserName = resolved.UserName ?? "",
        Password = resolved.Password ?? "",
        TimeoutSeconds = resolved.Timeout,
        CompletionCheckCommand = resolved.CompletionCheckCommand ?? "",
        CompletionPollIntervalSeconds = resolved.CompletionPollIntervalSeconds,
    };
}
