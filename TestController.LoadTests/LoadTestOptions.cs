namespace TestController.LoadTests;

/// <summary>
/// Parsed command-line options for the load/soak harness.
/// </summary>
public sealed record LoadTestOptions
{
    /// <summary>Base URL of the controller hosting <c>TestControllerService</c> (gRPC) and the REST diagnostics API.</summary>
    public required string ControllerGrpcUrl { get; init; }

    /// <summary>Base URL of the controller REST API (<c>/api/health/diagnostics</c>). Often the same host as <see cref="ControllerGrpcUrl"/>.</summary>
    public required string ControllerApiUrl { get; init; }

    /// <summary>Number of simulated agents to spin up.</summary>
    public int Agents { get; init; } = 100;

    /// <summary>How long to sustain the soak after all agents register.</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Heartbeat interval per agent (production default is 15s).</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>First localhost port for simulated agent gRPC servers; agent i listens on BasePort + i.</summary>
    public int BasePort { get; init; } = 6000;

    /// <summary>Hostname simulated agents advertise to the controller for callback (RunCommandStreamed).</summary>
    public string AgentHost { get; init; } = "127.0.0.1";

    /// <summary>How often to probe the controller diagnostics endpoint during the soak.</summary>
    public TimeSpan ProbeInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Output line rate streamed when the controller triggers a command on a simulated agent.</summary>
    public int OutputLinesPerSecond { get; init; } = 10;

    /// <summary>Maximum acceptable p95 latency for the diagnostics probe (controller-responsiveness gate).</summary>
    public TimeSpan MaxProbeP95 { get; init; } = TimeSpan.FromSeconds(2);

    public static LoadTestOptions Parse(string[] args)
    {
        string? grpc = null;
        string? api = null;
        int agents = 100;
        var duration = TimeSpan.FromMinutes(15);
        var heartbeat = TimeSpan.FromSeconds(15);
        int basePort = 6000;
        string agentHost = "127.0.0.1";
        var probe = TimeSpan.FromSeconds(5);
        int lps = 10;
        var maxP95 = TimeSpan.FromSeconds(2);

        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i]
                : throw new ArgumentException($"Missing value for '{args[i]}'");

            switch (args[i])
            {
                case "--controller":
                case "--controller-grpc": grpc = Next(); break;
                case "--controller-api": api = Next(); break;
                case "--agents": agents = int.Parse(Next()); break;
                case "--duration": duration = TimeSpan.Parse(Next()); break;
                case "--heartbeat": heartbeat = TimeSpan.Parse(Next()); break;
                case "--base-port": basePort = int.Parse(Next()); break;
                case "--agent-host": agentHost = Next(); break;
                case "--probe-interval": probe = TimeSpan.Parse(Next()); break;
                case "--output-lps": lps = int.Parse(Next()); break;
                case "--max-probe-p95": maxP95 = TimeSpan.Parse(Next()); break;
                case "-h":
                case "--help": throw new ArgumentException(HelpText);
                default: throw new ArgumentException($"Unknown argument '{args[i]}'.\n\n{HelpText}");
            }
        }

        grpc ??= "http://127.0.0.1:5000";
        api ??= grpc;

        return new LoadTestOptions
        {
            ControllerGrpcUrl = grpc,
            ControllerApiUrl = api,
            Agents = agents,
            Duration = duration,
            HeartbeatInterval = heartbeat,
            BasePort = basePort,
            AgentHost = agentHost,
            ProbeInterval = probe,
            OutputLinesPerSecond = lps,
            MaxProbeP95 = maxP95,
        };
    }

    public const string HelpText = """
        TestController.LoadTests — simulated-fleet load/soak harness.

        Usage:
          dotnet run --project TestController.LoadTests -- [options]

        Options:
          --controller <url>        Controller gRPC base URL (default http://127.0.0.1:5000)
          --controller-api <url>    Controller REST base URL (default: same as --controller)
          --agents <n>              Number of simulated agents (default 100)
          --duration <hh:mm:ss>     Soak duration after registration (default 00:15:00)
          --heartbeat <hh:mm:ss>    Per-agent heartbeat interval (default 00:00:15)
          --base-port <n>           First localhost port for agent servers (default 6000)
          --agent-host <host>       Hostname advertised to the controller (default 127.0.0.1)
          --probe-interval <hh:mm:ss>  Diagnostics probe cadence (default 00:00:05)
          --output-lps <n>          Output lines/sec streamed on trigger (default 10)
          --max-probe-p95 <hh:mm:ss>   Max acceptable probe p95 latency (default 00:00:02)

        Example (nightly 100-agent / 15-min profile):
          dotnet run -c Release --project TestController.LoadTests -- \
            --controller http://controller-host:5000 --agents 100 --duration 00:15:00
        """;
}
