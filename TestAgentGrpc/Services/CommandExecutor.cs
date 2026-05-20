using System.Diagnostics;
using System.Security;
using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Options;

namespace TestAgentGrpc.Services;

/// <summary>
/// Executes commands on the agent machine and streams every output line
/// through the <see cref="EventBroadcaster"/> in real time.
///
/// Compared to the legacy <c>TestAgentSvc.RunCommandInternal</c> which:
/// - Used <c>ThreadPool.QueueUserWorkItem</c> with a blocking lock
/// - Had stdout/stderr redirection commented out
/// - Only surfaced a single "Activity" string
///
/// This version:
/// - Streams stdout/stderr line-by-line as <see cref="ExecutionEvent"/> messages
/// - Provides a per-execution <see cref="Channel{T}"/> for <c>RunCommandStreamed</c>
/// - Tracks timing, exit codes, and resource metrics
/// - Maintains an execution history ring buffer
/// </summary>
public sealed class CommandExecutor : IDisposable
{
    private readonly EventBroadcaster _broadcaster;
    private readonly ExecutionTracker _tracker;
    private readonly AuditLogger _audit;
    private readonly AgentSettings _settings;
    private readonly ILogger<CommandExecutor> _logger;
    private readonly SemaphoreSlim _executionLock = new(1, 1);

    private Process? _currentProcess;
    private volatile AgentState _state = AgentState.Ready;
    private int _lastExitCode;
    private string _lastError = string.Empty;
    private string _activity = "Listening...";
    private string? _currentExecutionId;
    private string? _currentCommand;
    private DateTime? _executionStartedUtc;

    public event EventHandler<AgentState>? StateChanged;
    public event EventHandler<string>? ActivityChanged;

    public CommandExecutor(
        EventBroadcaster broadcaster,
        ExecutionTracker tracker,
        AuditLogger audit,
        IOptions<AgentSettings> settings,
        ILogger<CommandExecutor> logger)
    {
        _broadcaster = broadcaster;
        _tracker     = tracker;
        _audit       = audit;
        _settings    = settings.Value;
        _logger      = logger;
    }

    // ── Read-only state ────────────────────────────────────────────────

    public AgentState CurrentState       => _state;
    public int        LastExitCode       => _lastExitCode;
    public string     LastError          => _lastError;
    public string     Activity           => _activity;
    public string?    CurrentExecutionId => _currentExecutionId;
    public string?    CurrentCommand     => _currentCommand;
    public DateTime?  ExecutionStartedUtc => _executionStartedUtc;

    // ── Fire-and-forget RunCommand (legacy compatible) ─────────────────

    public (bool Accepted, string ExecutionId) RunCommand(
        string command, string arguments, bool isReboot,
        string? executionId = null, string? userName = null, string? password = null)
    {
        if (_state == AgentState.Running)
        {
            _logger.LogWarning("Agent is busy — rejecting: {Cmd}", command);
            return (false, string.Empty);
        }

        var execId = executionId ?? Guid.NewGuid().ToString("N")[..12];
        _ = Task.Run(() => ExecuteAsync(execId, command, arguments, isReboot,
            perCallChannel: null, CancellationToken.None, userName: userName, password: password));
        return (true, execId);
    }

    // ── Streamed RunCommand (new: returns a channel the caller reads) ──

    public (bool Accepted, string ExecutionId, ChannelReader<ExecutionEvent>? Stream) RunCommandStreamed(
        string command, string arguments, bool isReboot,
        int timeoutMs = 0,
        string? executionId = null, string? userName = null, string? password = null,
        string? completionCheckCommand = null, int completionPollIntervalSeconds = 30,
        CancellationToken externalCt = default)
    {
        if (_state == AgentState.Running)
            return (false, string.Empty, null);

        var execId = executionId ?? Guid.NewGuid().ToString("N")[..12];
        var ch = Channel.CreateUnbounded<ExecutionEvent>();

        // Build a CancellationToken that respects both the caller's token and the timeout
        CancellationTokenSource? cts;
        if (timeoutMs > 0)
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            cts.CancelAfter(timeoutMs);
        }
        else if (externalCt.CanBeCanceled)
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            // Safety-net: even with no explicit timeout, prevent a hung process
            // from leaving the agent permanently stuck in "busy" state.
            cts.CancelAfter(TimeSpan.FromMinutes(_settings.MaxExecutionTimeoutMinutes));
        }
        else
        {
            cts = new CancellationTokenSource(TimeSpan.FromMinutes(_settings.MaxExecutionTimeoutMinutes));
        }

        var ct = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteAsync(execId, command, arguments, isReboot, ch.Writer, ct,
                    userName: userName, password: password,
                    completionCheckCommand: completionCheckCommand,
                    completionPollIntervalSeconds: completionPollIntervalSeconds,
                    timeoutMs: timeoutMs);
            }
            finally
            {
                cts.Dispose();
            }
        });
        return (true, execId, ch.Reader);
    }

    // ── Core execution pipeline ────────────────────────────────────────

    private async Task ExecuteAsync(
        string executionId,
        string command,
        string arguments,
        bool isReboot,
        ChannelWriter<ExecutionEvent>? perCallChannel,
        CancellationToken ct,
        string? userName = null,
        string? password = null,
        string? completionCheckCommand = null,
        int completionPollIntervalSeconds = 30,
        int timeoutMs = 0)
    {
        // Acquire the execution lock — if cancelled here, we must NOT release in finally
        bool lockAcquired = false;
        try
        {
            await _executionLock.WaitAsync(ct);
            lockAcquired = true;
        }
        catch (OperationCanceledException)
        {
            perCallChannel?.TryComplete();
            throw;
        }

        _currentExecutionId = executionId;
        _currentCommand     = $"{command} {arguments}";
        _executionStartedUtc = DateTime.UtcNow;

        var record = _tracker.BeginTracked(executionId, command, arguments);

        try
        {
            // ── QUEUED ─────────────────────────────────────────────
            SetState(AgentState.Running);
            EmitEvent(executionId, ExecutionEventType.EventQueued,
                detail: $"Command queued: {command}", perCallChannel: perCallChannel);

            // ── START ──────────────────────────────────────────────
            var (resolvedFile, resolvedArgs) = ResolveInterpreter(command, arguments);

            var psi = new ProcessStartInfo
            {
                FileName               = resolvedFile,
                Arguments              = resolvedArgs,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            };

            // Apply credentials if provided (runs process as specified user)
            if (!string.IsNullOrWhiteSpace(userName))
            {
                ApplyCredentials(psi, userName, password);
                _logger.LogInformation("Running as user: {User}", psi.UserName);
            }

            _currentProcess = Process.Start(psi);
            if (_currentProcess is null)
            {
                throw new InvalidOperationException("Process.Start returned null");
            }

            _audit.Log("CommandStarted", executionId: executionId,
                command: resolvedFile, arguments: resolvedArgs,
                pid: _currentProcess.Id,
                detail: $"PID {_currentProcess.Id} launched as {resolvedFile}");

            EmitEvent(executionId, ExecutionEventType.EventStarted,
                command: command, arguments: arguments,
                detail: $"PID {_currentProcess.Id} launched",
                perCallChannel: perCallChannel);

            SetActivity($"Executing: {command} {arguments}");
            _lastError = string.Empty;

            // ── STREAM STDOUT + STDERR concurrently ────────────────
            var stdoutTask = StreamOutputAsync(executionId, _currentProcess.StandardOutput,
                OutputKind.OutputStdout, record, perCallChannel);
            var stderrTask = StreamOutputAsync(executionId, _currentProcess.StandardError,
                OutputKind.OutputStderr, record, perCallChannel);

            // ── HEARTBEAT for long-running silent processes ────────
            var process = _currentProcess;
            var heartbeatTask = Task.Run(async () =>
            {
                var startTime = DateTime.UtcNow;
                var timeoutTotal = timeoutMs > 0
                    ? TimeSpan.FromMilliseconds(timeoutMs)
                    : TimeSpan.FromHours(2);

                while (!ct.IsCancellationRequested && !process.HasExited)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    }
                    catch (OperationCanceledException) { break; }

                    if (process.HasExited) break;

                    var elapsed = DateTime.UtcNow - startTime;
                    var percent = Math.Min(99, (int)(elapsed.TotalSeconds / timeoutTotal.TotalSeconds * 100));
                    var elapsedStr = elapsed.ToString(@"mm\:ss");
                    var totalStr = timeoutTotal.ToString(@"mm\:ss");

                    var detail = $"Still running... (PID {process.Id}, {elapsedStr} / {totalStr})";

                    EmitEvent(executionId, ExecutionEventType.EventProgress,
                        detail: detail, progressPct: percent,
                        perCallChannel: perCallChannel);
                }

                // Final 100% when process exits
                if (process.HasExited)
                {
                    var elapsed = DateTime.UtcNow - startTime;
                    EmitEvent(executionId, ExecutionEventType.EventProgress,
                        detail: $"Process exited (exit code {process.ExitCode}, {elapsed:mm\\:ss} elapsed)",
                        progressPct: 100,
                        perCallChannel: perCallChannel);
                }
            }, ct);

            // ── WAIT FOR EXIT (cancellation-aware) ─────────────────
            await _currentProcess.WaitForExitAsync(ct);

            // Drain stdout/stderr with a timeout. After the main process exits,
            // child processes that inherited pipe handles can hold them open
            // indefinitely (e.g. services spawned by install scripts). We give
            // streams 30 seconds to drain, then proceed — this prevents the
            // agent from staying in "Running" state for 10+ minutes after the
            // main command has already finished.
            var drainTask = Task.WhenAll(stdoutTask, stderrTask);
            var drainTimeout = Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None);
            if (await Task.WhenAny(drainTask, drainTimeout) != drainTask)
            {
                _logger.LogWarning(
                    "Execution {Id}: stdout/stderr streams not drained after 30s (child processes may hold pipe handles). Proceeding with exit code.",
                    executionId);
            }
            // Heartbeat will self-terminate since HasExited is now true
            try { await heartbeatTask; } catch (OperationCanceledException) { }

            _lastExitCode = _currentProcess.ExitCode;

            // ── COMPLETION POLLING (child process monitoring) ──────
            if (!string.IsNullOrWhiteSpace(completionCheckCommand) && _lastExitCode == 0)
            {
                EmitEvent(executionId, ExecutionEventType.EventProgress,
                    detail: "Main process exited. Polling for child process completion...",
                    perCallChannel: perCallChannel);

                var pollInterval = Math.Max(completionPollIntervalSeconds, 5);
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(pollInterval), ct);

                    var checkResult = await RunCompletionCheckAsync(
                        completionCheckCommand, TimeSpan.FromSeconds(30), ct);

                    EmitEvent(executionId, ExecutionEventType.EventProgress,
                        detail: $"Completion check: {checkResult.Output.Trim()}",
                        perCallChannel: perCallChannel);

                    // findstr returns exit 1 when pattern NOT found = process no longer running
                    if (checkResult.ExitCode != 0 || checkResult.Output.Contains("DONE", StringComparison.OrdinalIgnoreCase))
                    {
                        EmitEvent(executionId, ExecutionEventType.EventProgress,
                            detail: "Child processes completed. Install finished.",
                            perCallChannel: perCallChannel);
                        break;
                    }
                }
            }

            record.Complete(_lastExitCode);

            var durationMs = (long)(DateTime.UtcNow - _executionStartedUtc!.Value).TotalMilliseconds;
            _audit.Log("CommandCompleted", executionId: executionId,
                command: command, arguments: arguments,
                exitCode: _lastExitCode, durationMs: durationMs,
                pid: _currentProcess.Id,
                detail: $"Exit code {_lastExitCode} after {durationMs}ms");

            // ── COMPLETED ──────────────────────────────────────────
            EmitEvent(executionId, ExecutionEventType.EventCompleted,
                exitCode: _lastExitCode,
                detail: $"Exit code {_lastExitCode}",
                perCallChannel: perCallChannel);

            _logger.LogInformation("Execution {Id} completed — exit {Code}",
                executionId, _lastExitCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation from timeout, safety-net, or controller disconnect — kill the
            // process tree and wait briefly for it to actually die before releasing the lock.
            KillCurrentProcess();
            try { _currentProcess?.WaitForExit(5000); }
            catch { /* process already disposed or exited */ }

            var elapsed = DateTime.UtcNow - _executionStartedUtc!.Value;
            var reason = timeoutMs > 0
                ? $"Execution timed out after {elapsed:hh\\:mm\\:ss} (limit: {timeoutMs}ms)"
                : $"Execution cancelled after {elapsed:hh\\:mm\\:ss} (safety-net {_settings.MaxExecutionTimeoutMinutes}min or client disconnect)";

            _lastExitCode = -1;
            _lastError = reason;
            record.Fail(_lastError);

            _audit.Log("CommandTerminated", severity: "Warning",
                executionId: executionId, command: command, arguments: arguments,
                exitCode: -1, detail: _lastError);

            EmitEvent(executionId, ExecutionEventType.EventFailed,
                exitCode: -1,
                errorMessage: _lastError,
                detail: _lastError,
                perCallChannel: perCallChannel);

            _logger.LogWarning("Execution {Id} cancelled — {Reason}", executionId, reason);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            record.Fail(ex.Message);

            _audit.Log("CommandFailed", severity: "Error",
                executionId: executionId, command: command, arguments: arguments,
                detail: ex.Message);

            EmitEvent(executionId, ExecutionEventType.EventFailed,
                errorMessage: ex.Message,
                detail: $"Execution failed: {ex.Message}",
                perCallChannel: perCallChannel);

            _logger.LogError(ex, "Execution {Id} failed", executionId);
        }
        finally
        {
            // CRITICAL: Guarantee the agent always returns to Ready state and releases
            // the execution lock — even if individual cleanup steps throw (e.g. Process.Dispose()
            // on an invalid handle, or an event handler throwing in SetActivity/StateChanged).
            try { _currentProcess?.Dispose(); } catch { /* best effort */ }
            _currentProcess = null;
            _currentExecutionId = null;
            _currentCommand = null;
            _executionStartedUtc = null;

            try
            {
                SetActivity(isReboot
                    ? $"Reboot pending: {command} {arguments}"
                    : $"Finished: {command} {arguments}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SetActivity threw during cleanup");
            }

            // Set the volatile state field FIRST so even if the event handler
            // throws, the agent no longer appears busy to incoming requests.
            _state = AgentState.Ready;
            try { StateChanged?.Invoke(this, AgentState.Ready); } catch { /* swallow */ }
            try
            {
                _broadcaster.Publish(BuildEvent(
                    executionId,
                    ExecutionEventType.EventStateChanged,
                    agentState: AgentState.Ready,
                    detail: "State → Ready"));
            }
            catch { /* swallow */ }

            perCallChannel?.TryComplete();

            if (lockAcquired)
                _executionLock.Release();
        }
    }

    /// <summary>
    /// Runs a quick command to check if child processes have completed.
    /// Returns the exit code and captured stdout for decision-making.
    /// </summary>
    private async Task<(int ExitCode, string Output)> RunCompletionCheckAsync(
        string command, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var (resolvedFile, resolvedArgs) = ResolveInterpreter(command, "");

            var psi = new ProcessStartInfo
            {
                FileName = resolvedFile,
                Arguments = resolvedArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var checkProcess = Process.Start(psi);
            if (checkProcess is null)
                return (-1, "Failed to start check process");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            var output = await checkProcess.StandardOutput.ReadToEndAsync(cts.Token);
            await checkProcess.WaitForExitAsync(cts.Token);

            return (checkProcess.ExitCode, output);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Completion check command failed");
            return (-1, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads a stream (stdout or stderr) line-by-line and emits an event
    /// for each line — this is what enables real-time output monitoring.
    /// </summary>
    private async Task StreamOutputAsync(
        string executionId,
        StreamReader reader,
        OutputKind kind,
        ExecutionTracker.ExecutionRecordBuilder record,
        ChannelWriter<ExecutionEvent>? perCallChannel)
    {
        var eventType = kind == OutputKind.OutputStdout
            ? ExecutionEventType.EventStdoutLine
            : ExecutionEventType.EventStderrLine;

        while (await reader.ReadLineAsync() is { } line)
        {
            record.AddOutputLine(kind, line);

            EmitEvent(executionId, eventType,
                outputLine: line,
                outputKind: kind,
                errorMessage: kind == OutputKind.OutputStderr ? line : null,
                perCallChannel: perCallChannel);
        }
    }

    // ── Terminate ──────────────────────────────────────────────────────

    public void TerminateExecution()
    {
        try
        {
            KillCurrentProcess();
            if (_currentExecutionId is not null)
            {
                _audit.Log("CommandTerminated", severity: "Warning",
                    executionId: _currentExecutionId,
                    detail: "Terminated by controller request");

                EmitEvent(_currentExecutionId, ExecutionEventType.EventTerminated,
                    detail: "Terminated by controller request");
            }
            _tracker.GetCurrent()?.Terminate();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TerminateExecution failed");
            _lastError = ex.Message;
        }
    }

    /// <summary>
    /// Emergency reset: kills the current process, forcibly sets state to Ready,
    /// and releases the execution lock. Use when the agent is permanently stuck.
    /// </summary>
    public void ForceReady()
    {
        _logger.LogWarning("ForceReady invoked — forcibly resetting agent state");
        try { KillCurrentProcess(); } catch { /* best effort */ }
        try { _currentProcess?.Dispose(); } catch { /* best effort */ }
        _currentProcess = null;
        _currentExecutionId = null;
        _currentCommand = null;
        _executionStartedUtc = null;

        _state = AgentState.Ready;
        try { StateChanged?.Invoke(this, AgentState.Ready); } catch { /* swallow */ }

        // Release the lock if it's currently held (count will be 0 when held)
        if (_executionLock.CurrentCount == 0)
        {
            try { _executionLock.Release(); } catch { /* already released */ }
        }

        _audit.Log("ForceReady", severity: "Warning",
            detail: "Agent state forcibly reset to Ready");
    }

    /// <summary>
    /// Kills the current process tree if it's still running.
    /// Safe to call multiple times or when no process is active.
    /// </summary>
    private void KillCurrentProcess()
    {
        try
        {
            if (_currentProcess is { HasExited: false } proc)
            {
                _logger.LogWarning("Killing PID {Pid}", proc.Id);
                proc.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited between the check and the kill — safe to ignore
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to kill process");
        }
    }

    // ── Script interpreter resolution ───────────────────────────────────

    /// <summary>
    /// Resolves the correct interpreter for script files.
    /// .bat/.cmd  → cmd.exe /c "command" args
    /// .ps1       → powershell.exe -ExecutionPolicy Bypass -NoProfile -File "command" args
    /// Everything else → used directly as FileName.
    /// </summary>
    private static (string FileName, string Arguments) ResolveInterpreter(string command, string arguments)
    {
        var cmd = command.Trim().Trim('"');
        var ext = "";
        try { ext = Path.GetExtension(cmd).ToLowerInvariant(); } catch { }

        return ext switch
        {
            ".bat" or ".cmd" =>
                ("cmd.exe", $"/c \"{cmd}\" {arguments}".TrimEnd()),
            ".ps1" =>
                ("powershell.exe", $"-ExecutionPolicy Bypass -NoProfile -File \"{cmd}\" {arguments}".TrimEnd()),
            _ =>
                (command, arguments),
        };
    }

    /// <summary>
    /// Parses credentials and applies them to the <see cref="ProcessStartInfo"/>.
    ///
    /// Handles formats:
    ///   - <c>DOMAIN\user</c>  → Domain = DOMAIN,  UserName = user
    ///   - <c>user@domain</c>  → Domain = domain,   UserName = user
    ///   - <c>user</c>         → Domain = ".",       UserName = user  (local machine)
    ///
    /// Also sets <c>WorkingDirectory</c> to a universally accessible path
    /// when not already set — required by <c>CreateProcessWithLogonW</c>.
    /// </summary>
    private static void ApplyCredentials(ProcessStartInfo psi, string userName, string? password)
    {
        // ── Parse domain\user or user@domain ───────────────────────
        string domain;
        string user;

        if (userName.Contains('\\'))
        {
            var parts = userName.Split('\\', 2);
            domain = parts[0];
            user = parts[1];
        }
        else if (userName.Contains('@'))
        {
            var parts = userName.Split('@', 2);
            user = parts[0];
            domain = parts[1];
        }
        else
        {
            // No domain specified — assume local machine
            domain = ".";
            user = userName;
        }

        psi.Domain = domain;
        psi.UserName = user;

        // ── Password — only set SecureString when a real password is provided ──
        if (!string.IsNullOrEmpty(password))
        {
            var secure = new SecureString();
            foreach (char c in password)
                secure.AppendChar(c);
            secure.MakeReadOnly();
            psi.Password = secure;
        }

        // ── Working directory — CreateProcessWithLogonW requires an
        //    accessible working directory for the target user.
        //    Fall back to a well-known writable path if not already set.
        if (string.IsNullOrEmpty(psi.WorkingDirectory))
        {
            // Prefer C:\Windows\Temp (accessible to all authenticated users)
            var systemTemp = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
            psi.WorkingDirectory = Directory.Exists(systemTemp)
                ? systemTemp
                : Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        }
    }

    // ── State / activity helpers ───────────────────────────────────────

    public void SetState(AgentState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(this, state);

        _broadcaster.Publish(BuildEvent(
            _currentExecutionId ?? "",
            ExecutionEventType.EventStateChanged,
            agentState: state,
            detail: $"State → {state}"));
    }

    private void SetActivity(string activity)
    {
        _activity = activity;
        _logger.LogInformation("Activity: {Activity}", activity);
        ActivityChanged?.Invoke(this, activity);
    }

    // ── Event construction ─────────────────────────────────────────────

    private void EmitEvent(string executionId, ExecutionEventType type,
        string? outputLine = null, OutputKind? outputKind = null,
        int? exitCode = null, string? errorMessage = null,
        AgentState? agentState = null, string? command = null,
        string? arguments = null, string? detail = null,
        double? progressPct = null,
        ChannelWriter<ExecutionEvent>? perCallChannel = null)
    {
        var evt = BuildEvent(executionId, type, outputLine, outputKind,
            exitCode, errorMessage, agentState, command, arguments, detail,
            progressPct);

        _broadcaster.Publish(evt);
        perCallChannel?.TryWrite(evt);
    }

    private ExecutionEvent BuildEvent(string executionId, ExecutionEventType type,
        string? outputLine = null, OutputKind? outputKind = null,
        int? exitCode = null, string? errorMessage = null,
        AgentState? agentState = null, string? command = null,
        string? arguments = null, string? detail = null,
        double? progressPct = null)
    {
        var evt = new ExecutionEvent
        {
            ExecutionId = executionId,
            AgentName   = _settings.AgentName,
            Timestamp   = Timestamp.FromDateTime(DateTime.UtcNow),
            EventType   = type,
        };
        if (outputLine   is not null) evt.OutputLine   = outputLine;
        if (outputKind   is not null) evt.OutputKind    = outputKind.Value;
        if (exitCode     is not null) evt.ExitCode      = exitCode.Value;
        if (errorMessage is not null) evt.ErrorMessage  = errorMessage;
        if (agentState   is not null) evt.AgentState    = agentState.Value;
        if (command      is not null) evt.Command       = command;
        if (arguments    is not null) evt.Arguments     = arguments;
        if (detail       is not null) evt.Detail        = detail;
        if (progressPct  is not null) evt.ProgressPct   = progressPct.Value;
        return evt;
    }

    public void Dispose()
    {
        _currentProcess?.Dispose();
        _executionLock.Dispose();
    }
}
