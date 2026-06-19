namespace TestController.LoadTests;

/// <summary>
/// Aggregates probe samples over the soak and evaluates the measurable subset of
/// the SCALING-IMPLEMENTATION-PLAN success criteria into a pass/fail gate.
///
/// Browser-side criteria (DOM node count, SignalR msg/s, ThreadPool starvation
/// counters) are intentionally out of scope here — per the plan, those are
/// observed via dotnet-counters / DevTools, and a missed target is fixed in the
/// owning scaling phase, not patched in the harness.
/// </summary>
public sealed class LoadTestReport
{
    private readonly List<double> _latencies = new();
    private readonly List<int> _agentCounts = new();
    private int _http429;
    private int _probeErrors;

    public int ExpectedAgents { get; init; }
    public TimeSpan MaxProbeP95 { get; init; }

    public void Record(ProbeSample sample)
    {
        if (sample.StatusCode == 429) _http429++;
        if (sample.StatusCode is < 200 or >= 300) _probeErrors++;
        else
        {
            _latencies.Add(sample.LatencyMs);
            _agentCounts.Add(sample.SimAgentCount);
        }
    }

    public void RecordProbeException() => _probeErrors++;

    private static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        var rank = (int)Math.Ceiling(p / 100.0 * sorted.Count) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }

    /// <returns>True if every measured criterion passed.</returns>
    public bool Print(
        int registeredAtPeak,
        long heartbeatsSent,
        long heartbeatFailures,
        TimeSpan soakDuration)
    {
        var sorted = _latencies.OrderBy(x => x).ToList();
        var min = sorted.Count > 0 ? sorted[0] : 0;
        var max = sorted.Count > 0 ? sorted[^1] : 0;
        var avg = sorted.Count > 0 ? sorted.Average() : 0;
        var p95 = Percentile(sorted, 95);
        var minSeen = _agentCounts.Count > 0 ? _agentCounts.Min() : 0;
        var maxSeen = _agentCounts.Count > 0 ? _agentCounts.Max() : 0;

        Console.WriteLine();
        Console.WriteLine("================ Load/Soak Report ================");
        Console.WriteLine($"  Soak duration            : {soakDuration}");
        Console.WriteLine($"  Expected agents          : {ExpectedAgents}");
        Console.WriteLine($"  Registered at peak       : {registeredAtPeak}");
        Console.WriteLine($"  Sim agents seen (min/max): {minSeen} / {maxSeen}");
        Console.WriteLine($"  Heartbeats sent          : {heartbeatsSent} (failures: {heartbeatFailures})");
        Console.WriteLine($"  Diagnostics probes       : {_latencies.Count} ok, {_probeErrors} errors, {_http429} HTTP 429");
        Console.WriteLine($"  Probe latency ms         : min {min:F1} | avg {avg:F1} | p95 {p95:F1} | max {max:F1}");
        Console.WriteLine("--------------------------------------------------");

        var criteria = new List<(string Name, bool Pass, string Detail)>
        {
            ("All agents registered",
                registeredAtPeak >= ExpectedAgents,
                $"{registeredAtPeak}/{ExpectedAgents}"),
            ("Fleet visibility stable (no mass drop)",
                _agentCounts.Count > 0 && minSeen >= (int)(ExpectedAgents * 0.95),
                $"min seen {minSeen} (>= 95% of {ExpectedAgents})"),
            ("Controller responsive (probe p95)",
                p95 <= MaxProbeP95.TotalMilliseconds,
                $"p95 {p95:F1}ms <= {MaxProbeP95.TotalMilliseconds:F0}ms"),
            ("Zero HTTP 429 (rate-limit) events",
                _http429 == 0,
                $"{_http429} observed"),
            ("Zero probe errors",
                _probeErrors == 0,
                $"{_probeErrors} observed"),
            ("Heartbeats delivered",
                heartbeatsSent > 0 && heartbeatFailures == 0,
                $"{heartbeatsSent} sent / {heartbeatFailures} failed"),
        };

        var allPass = true;
        foreach (var (name, pass, detail) in criteria)
        {
            allPass &= pass;
            Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name,-40} {detail}");
        }

        Console.WriteLine("==================================================");
        Console.WriteLine($"  RESULT: {(allPass ? "PASS" : "FAIL")}");
        Console.WriteLine("==================================================");
        Console.WriteLine();
        Console.WriteLine("Note: DOM-node, SignalR msg/s, and ThreadPool-starvation criteria are");
        Console.WriteLine("observed separately (DevTools / dotnet-counters) per the scaling plan.");
        return allPass;
    }
}
