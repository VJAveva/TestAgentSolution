using System.Diagnostics;
using System.Net.NetworkInformation;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Maintenance;

/// <summary>
/// Confirms a node has come back after a revert. Two independent probes: raw ICMP reachability (the machine
/// booted) and a live gRPC round-trip (the agent process is answering). Success is confirmed by the agent
/// responding, never by a script exit code. (Spec: FleetRevert §2, §4 PingWait/AgentWait, Prompt 3.)
/// </summary>
public sealed class NodeReadinessProbe : INodeReadinessProbe
{
    private const int PingReplyTimeoutMs = 4000;
    private static readonly TimeSpan AgentPollInterval = TimeSpan.FromSeconds(3);

    private readonly IAgentGrpcDispatcher _dispatcher;

    public NodeReadinessProbe(IAgentGrpcDispatcher dispatcher) => _dispatcher = dispatcher;

    public async Task<ReadinessResult> WaitForPingAsync(string host, PingOptions options, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await Task.Delay(options.BootDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ReadinessResult(false, stopwatch.Elapsed, "Cancelled during the boot delay.");
        }

        using var ping = new Ping();
        var consecutive = 0;

        while (stopwatch.Elapsed < options.Timeout)
        {
            if (cancellationToken.IsCancellationRequested)
                return new ReadinessResult(false, stopwatch.Elapsed, "Cancelled while waiting for ping replies.");

            try
            {
                var reply = await ping.SendPingAsync(host, PingReplyTimeoutMs).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success)
                {
                    consecutive++;
                    if (consecutive >= options.RequiredConsecutiveReplies)
                        return new ReadinessResult(true, stopwatch.Elapsed, null);
                }
                else
                {
                    consecutive = 0;  // a single miss resets the streak
                }
            }
            catch (PingException)
            {
                consecutive = 0;  // host not resolvable / reachable yet
            }

            try
            {
                await Task.Delay(options.Interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new ReadinessResult(false, stopwatch.Elapsed, "Cancelled while waiting for ping replies.");
            }
        }

        return new ReadinessResult(false, stopwatch.Elapsed,
            $"Host '{host}' did not return {options.RequiredConsecutiveReplies} consecutive ping replies within {options.Timeout}.");
    }

    // A successful gRPC ping is a live round-trip, so it cannot be satisfied by a stale registration record —
    // that is why no notBeforeUtc reference point is needed here (cf. Prompt 3's registry-timestamp approach).
    public async Task<ReadinessResult> WaitForAgentAsync(string nodeId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < timeout)
        {
            if (cancellationToken.IsCancellationRequested)
                return new ReadinessResult(false, stopwatch.Elapsed, "Cancelled while waiting for the agent to reconnect.");

            try
            {
                if (await _dispatcher.PingAsync(nodeId, cancellationToken).ConfigureAwait(false))
                    return new ReadinessResult(true, stopwatch.Elapsed, null);
            }
            catch (OperationCanceledException)
            {
                return new ReadinessResult(false, stopwatch.Elapsed, "Cancelled while waiting for the agent to reconnect.");
            }
            catch
            {
                // Agent not answering yet (channel down, boot in progress); keep polling.
            }

            try
            {
                await Task.Delay(AgentPollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new ReadinessResult(false, stopwatch.Elapsed, "Cancelled while waiting for the agent to reconnect.");
            }
        }

        return new ReadinessResult(false, stopwatch.Elapsed, $"Agent '{nodeId}' did not reconnect within {timeout}.");
    }
}
