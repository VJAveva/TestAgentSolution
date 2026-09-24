using TestControllerGrpc.Core.Maintenance;

namespace TestControllerGrpc.Tests.Maintenance;

/// <summary>
/// Wave 0: this runner had ZERO coverage, yet it is the only controller-side execution path in the system —
/// a revert cannot run on the agent because the VM under it is destroyed mid-command. These drive the real
/// powershell.exe, because the behaviour that matters (exit codes, stream tagging, kill-on-timeout) lives in
/// the process plumbing and a mocked process would prove none of it.
/// </summary>
public sealed class PowerShellScriptRunnerTests : IDisposable
{
    private readonly List<string> _scripts = [];

    private string ScriptFile(string body)
    {
        string path = Path.Combine(Path.GetTempPath(), $"tc-pssr-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(path, body);
        _scripts.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (string path in _scripts)
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    private sealed class Collector : IProgress<ScriptOutputLine>
    {
        private readonly List<ScriptOutputLine> _lines = [];
        private readonly Lock _gate = new();

        public void Report(ScriptOutputLine value)
        {
            lock (_gate) _lines.Add(value);
        }

        public IReadOnlyList<ScriptOutputLine> Lines
        {
            get { lock (_gate) return _lines.ToList(); }
        }
    }

    /// <summary>Completes on the script's first output line — the only reliable proof the process is running.</summary>
    private sealed class StartSignal : IProgress<ScriptOutputLine>
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public void Report(ScriptOutputLine value) => _started.TrySetResult();
    }

    [Fact]
    public async Task RunAsync_Should_ReportExitCode_When_TheScriptFails()
    {
        // F-5 is exactly this shape: the revert script exits 2 when vCloud credentials are missing, and the
        // caller decides what that means. A thrown exception here would quarantine the node instead.
        var runner = new PowerShellScriptRunner();
        var invocation = new ScriptInvocation { ScriptPath = ScriptFile("exit 2") };

        ScriptResult result = await runner.RunAsync(invocation, new Collector(), CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public async Task RunAsync_Should_ReportZero_When_TheScriptSucceeds()
    {
        var runner = new PowerShellScriptRunner();
        var invocation = new ScriptInvocation { ScriptPath = ScriptFile("exit 0") };

        ScriptResult result = await runner.RunAsync(invocation, new Collector(), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public async Task RunAsync_Should_TagEachStreamSeparately_When_BothAreWritten()
    {
        var runner = new PowerShellScriptRunner();
        var collector = new Collector();
        var invocation = new ScriptInvocation
        {
            ScriptPath = ScriptFile("[Console]::Out.WriteLine('out-line'); [Console]::Error.WriteLine('err-line'); exit 0"),
        };

        await runner.RunAsync(invocation, collector, CancellationToken.None);

        Assert.Contains(collector.Lines, l => l.Stream == ScriptStream.Stdout && l.Text.Contains("out-line", StringComparison.Ordinal));
        Assert.Contains(collector.Lines, l => l.Stream == ScriptStream.Stderr && l.Text.Contains("err-line", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_Should_PreserveArgumentsContainingSpaces()
    {
        // ArgumentList escapes each value independently; a snapshot name with spaces must arrive as ONE argument.
        var runner = new PowerShellScriptRunner();
        var collector = new Collector();
        var invocation = new ScriptInvocation
        {
            ScriptPath = ScriptFile("param($a, $b) [Console]::Out.WriteLine(\"A=[$a] COUNT=$($args.Count)\"); exit 0"),
            Arguments = ["Golden Image 2026 R2", "second"],
        };

        await runner.RunAsync(invocation, collector, CancellationToken.None);

        Assert.Contains(collector.Lines, l => l.Text.Contains("A=[Golden Image 2026 R2]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_Should_KillTheScript_When_TheTimeoutElapses()
    {
        var runner = new PowerShellScriptRunner();
        var invocation = new ScriptInvocation
        {
            ScriptPath = ScriptFile("Start-Sleep -Seconds 60; exit 0"),
            Timeout = TimeSpan.FromSeconds(2),
        };

        var watch = System.Diagnostics.Stopwatch.StartNew();
        ScriptResult result = await runner.RunAsync(invocation, new Collector(), CancellationToken.None);
        watch.Stop();

        Assert.True(result.Cancelled);
        // Returning only after the full 60s would mean the kill never happened.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed} — the process tree was not killed");
    }

    [Fact]
    public async Task RunAsync_Should_KillTheScript_When_TheCallerCancels()
    {
        var runner = new PowerShellScriptRunner();
        // Cancelling on a wall clock races powershell.exe's cold start, which under full-suite load can exceed
        // any fixed delay. Wait for the marker line, so the stopwatch measures kill latency and nothing else.
        var invocation = new ScriptInvocation { ScriptPath = ScriptFile("'running'; Start-Sleep -Seconds 60; exit 0") };
        var progress = new StartSignal();
        using var cts = new CancellationTokenSource();

        Task<ScriptResult> run = runner.RunAsync(invocation, progress, cts.Token);
        await progress.Started.WaitAsync(TimeSpan.FromSeconds(60));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        cts.Cancel();
        ScriptResult result = await run;
        watch.Stop();

        Assert.True(result.Cancelled);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed} — cancellation did not kill the process");
    }

    [Fact]
    public async Task RunAsync_Should_ReportNonZero_When_TheScriptPathDoesNotExist()
    {
        // A missing script must surface as a failed result, not an unhandled exception in the maintenance engine.
        var runner = new PowerShellScriptRunner();
        var invocation = new ScriptInvocation
        {
            ScriptPath = Path.Combine(Path.GetTempPath(), $"tc-missing-{Guid.NewGuid():N}.ps1"),
        };

        ScriptResult result = await runner.RunAsync(invocation, new Collector(), CancellationToken.None);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(result.Cancelled);
    }
}
