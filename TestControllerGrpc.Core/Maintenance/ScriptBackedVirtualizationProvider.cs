using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Maintenance;

/// <summary>Where a platform's verb-dispatched <c>Vm-Ops.*.ps1</c> lives, and how long it may run.</summary>
public sealed record VirtualizationProviderOptions
{
    public required string PlatformId { get; init; }
    public required string ScriptPath { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);
}

/// <summary>
/// Drives a per-platform PowerShell script through the existing <see cref="IPowerShellScriptRunner"/>.
/// Orchestration (phases, retry, cancellation) stays in C# where it is testable; PowerCLI and the Hyper-V
/// module stay in PowerShell where they live. One verb-dispatched script per platform, not six per platform.
/// </summary>
public sealed class ScriptBackedVirtualizationProvider : IVirtualizationProvider
{
    private readonly IPowerShellScriptRunner _runner;
    private readonly VirtualizationProviderOptions _options;
    private readonly IAppLogger _logger;

    public ScriptBackedVirtualizationProvider(
        IPowerShellScriptRunner runner,
        VirtualizationProviderOptions options,
        VmPlatformCapabilities capabilities,
        IAppLogger logger)
    {
        _runner = runner;
        _options = options;
        Capabilities = capabilities;
        _logger = logger;
    }

    public string PlatformId => _options.PlatformId;

    public VmPlatformCapabilities Capabilities { get; }

    public Task<VmOpResult> GetPowerStateAsync(IReadOnlyList<string> vmNames, CancellationToken ct) =>
        InvokeAsync("State", vmNames, [], ct);

    public Task<VmOpResult> PowerOnAsync(IReadOnlyList<string> vmNames, CancellationToken ct) =>
        InvokeAsync("PowerOn", vmNames, [], ct);

    public Task<VmOpResult> PowerOffAsync(IReadOnlyList<string> vmNames, bool graceful, CancellationToken ct) =>
        InvokeAsync("PowerOff", vmNames, ["-Graceful", graceful ? "true" : "false"], ct);

    public async Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(string vmName, CancellationToken ct)
    {
        var result = await InvokeAsync("List", [vmName], [], ct).ConfigureAwait(false);
        return result.Results
            .Where(r => r.Ok && r.Snapshot is not null)
            .Select(r => r.Snapshot!)
            .ToList();
    }

    public Task<VmOpResult> CreateSnapshotAsync(
        IReadOnlyList<string> vmNames, string snapshotName, string description, CancellationToken ct) =>
        InvokeAsync("Create", vmNames, ["-Snapshot", snapshotName, "-Description", description], ct);

    public Task<VmOpResult> RevertSnapshotAsync(
        IReadOnlyList<string> vmNames, string? snapshotName, CancellationToken ct)
    {
        // The name is meaningless where the platform holds a single unnamed snapshot. Warn rather than accept it
        // silently: the previous contract took a name, ignored it, and left callers believing it had an effect.
        string[] extra = [];
        if (!string.IsNullOrWhiteSpace(snapshotName))
        {
            if (Capabilities.SupportsNamedSnapshots)
                extra = ["-Snapshot", snapshotName];
            else
                _logger.Warn("Virtualization",
                    $"Snapshot name '{snapshotName}' ignored: {PlatformId} keeps a single unnamed snapshot per VM.");
        }

        return InvokeAsync("Revert", vmNames, extra, ct);
    }

    public Task<VmOpResult> DeleteSnapshotAsync(string vmName, string snapshotId, CancellationToken ct) =>
        InvokeAsync("Delete", [vmName], ["-SnapshotId", snapshotId], ct);

    private async Task<VmOpResult> InvokeAsync(
        string verb, IReadOnlyList<string> vmNames, IReadOnlyList<string> extraArgs, CancellationToken ct)
    {
        if (vmNames.Count == 0) return new VmOpResult([]);

        if (!Capabilities.SupportsBatchOperations && vmNames.Count > 1)
            return await InvokeOneAtATimeAsync(verb, vmNames, extraArgs, ct).ConfigureAwait(false);

        var args = new List<string> { "-Verb", verb, "-Vm", string.Join(",", vmNames) };
        args.AddRange(extraArgs);

        var stdout = new List<string>();
        var progress = new SyncProgress<ScriptOutputLine>(line =>
        {
            if (line.Stream == ScriptStream.Stdout) stdout.Add(line.Text);
        });

        ScriptResult script;
        try
        {
            script = await _runner.RunAsync(
                new ScriptInvocation
                {
                    ScriptPath = _options.ScriptPath,
                    Arguments = args,
                    Timeout = _options.Timeout,
                },
                progress,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error("Virtualization", $"{PlatformId} {verb} failed to launch.", ex);
            return VmOpResult.Failed(vmNames, ex.Message);
        }

        var envelope = VmOpsEnvelopeParser.Parse(stdout);
        if (envelope is null)
        {
            // Exit code is the coarse fallback, kept so a script that predates the JSON contract still reports.
            var reason = script.Cancelled
                ? "Script cancelled before reporting a result."
                : $"Script produced no JSON result (exit code {script.ExitCode}).";
            _logger.Warn("Virtualization", $"{PlatformId} {verb}: {reason}");
            return script is { ExitCode: 0, Cancelled: false }
                ? new VmOpResult([.. vmNames.Select(v => new VmResult(v, true))])
                : VmOpResult.Failed(vmNames, reason);
        }

        return VmOpsEnvelopeParser.ToResult(envelope, vmNames);
    }

    // Serial fallback for a platform that cannot batch; results are merged so callers see one shape either way.
    private async Task<VmOpResult> InvokeOneAtATimeAsync(
        string verb, IReadOnlyList<string> vmNames, IReadOnlyList<string> extraArgs, CancellationToken ct)
    {
        var merged = new List<VmResult>();
        foreach (var vm in vmNames)
        {
            ct.ThrowIfCancellationRequested();
            var single = await InvokeAsync(verb, [vm], extraArgs, ct).ConfigureAwait(false);
            merged.AddRange(single.Results);
        }
        return new VmOpResult(merged);
    }
}
