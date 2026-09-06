using System.Diagnostics;

namespace TestControllerGrpc.Core.Maintenance;

/// <summary>
/// Runs a PowerShell (or batch) script on the controller and streams its output line by line. A revert cannot
/// run on the agent — the VM the agent lives on is destroyed mid-command — so this controller-side runner is a
/// net-new execution path (the rest of the system dispatches commands to agents). (Spec: FleetRevert §2, Prompt 2.)
/// </summary>
public sealed class PowerShellScriptRunner : IPowerShellScriptRunner
{
    public async Task<ScriptResult> RunAsync(
        ScriptInvocation invocation,
        IProgress<ScriptOutputLine> output,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (!string.IsNullOrWhiteSpace(invocation.WorkingDirectory))
            startInfo.WorkingDirectory = invocation.WorkingDirectory;

        // ArgumentList escapes each value on its own, so a snapshot name containing spaces survives intact.
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(invocation.ScriptPath);
        foreach (var argument in invocation.Arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                output.Report(new ScriptOutputLine(DateTimeOffset.UtcNow, ScriptStream.Stdout, e.Data));
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                output.Report(new ScriptOutputLine(DateTimeOffset.UtcNow, ScriptStream.Stderr, e.Data));
        };

        var stopwatch = Stopwatch.StartNew();

        process.Start();
        process.BeginOutputReadLine();  // async reads on both streams together — the non-deadlocking pattern
        process.BeginErrorReadLine();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (invocation.Timeout is { } timeout && timeout > TimeSpan.Zero)
            linkedCts.CancelAfter(timeout);

        var cancelled = false;
        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            TryKillProcessTree(process);
            // Drain to real exit (no token) so redirected output flushes and ExitCode is readable.
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* process already gone */ }
        }

        stopwatch.Stop();

        int exitCode;
        try { exitCode = process.ExitCode; }
        catch { exitCode = -1; }

        // A non-zero exit code is reported, never thrown — the caller decides what it means.
        return new ScriptResult(exitCode, stopwatch.Elapsed, cancelled);
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Raced with natural exit between HasExited and Kill; nothing left to do.
        }
    }
}
