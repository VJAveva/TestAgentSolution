using System.Diagnostics;
using TestController.LoadTests;

// D3 simulated-fleet load/soak harness entry point.
// Manual / nightly only — see README.md and TestController.LoadTests/LoadTestOptions.HelpText.

LoadTestOptions opts;
try
{
    opts = LoadTestOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

Console.WriteLine($"Load harness starting: {opts.Agents} agents -> {opts.ControllerGrpcUrl}");
Console.WriteLine($"  Soak {opts.Duration}, heartbeat {opts.HeartbeatInterval}, ports {opts.BasePort}..{opts.BasePort + opts.Agents - 1}");

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
var ct = shutdown.Token;

var agents = new List<SimulatedAgent>(opts.Agents);
var report = new LoadTestReport { ExpectedAgents = opts.Agents, MaxProbeP95 = opts.MaxProbeP95 };

try
{
    // ---- Ramp up: start + register agents (bounded concurrency) ----
    var rampSw = Stopwatch.StartNew();
    var started = 0;
    using (var gate = new SemaphoreSlim(20))
    {
        var startTasks = Enumerable.Range(0, opts.Agents).Select(async i =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var agent = new SimulatedAgent(opts, i);
                await agent.StartAsync(ct);
                lock (agents) agents.Add(agent);
                var n = Interlocked.Increment(ref started);
                if (n % 25 == 0 || n == opts.Agents)
                    Console.WriteLine($"  registered {n}/{opts.Agents} agents ({rampSw.Elapsed:mm\\:ss})");
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(startTasks);
    }
    rampSw.Stop();
    Console.WriteLine($"Ramp-up complete: {agents.Count} agents in {rampSw.Elapsed:mm\\:ss}.");

    // ---- Soak: probe the controller on a fixed cadence ----
    var probe = new ControllerProbe(opts.ControllerApiUrl);
    var registeredAtPeak = 0;
    var soakEnd = DateTime.UtcNow + opts.Duration;

    while (DateTime.UtcNow < soakEnd && !ct.IsCancellationRequested)
    {
        try
        {
            var sample = await probe.SampleAsync(ct);
            report.Record(sample);
            registeredAtPeak = Math.Max(registeredAtPeak, sample.SimAgentCount);
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex)
        {
            report.RecordProbeException();
            Console.Error.WriteLine($"  probe error: {ex.Message}");
        }

        try { await Task.Delay(opts.ProbeInterval, ct); }
        catch (OperationCanceledException) { break; }
    }

    // ---- Report ----
    var heartbeatsSent = agents.Sum(a => a.HeartbeatsSent);
    var heartbeatFailures = agents.Sum(a => a.HeartbeatFailures);
    var pass = report.Print(registeredAtPeak, heartbeatsSent, heartbeatFailures, opts.Duration);
    return pass ? 0 : 1;
}
finally
{
    Console.WriteLine("Tearing down agents...");
    await Task.WhenAll(agents.Select(async a =>
    {
        try { await a.DisposeAsync(); } catch { /* best-effort */ }
    }));
}
