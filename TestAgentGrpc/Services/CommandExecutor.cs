using System.Diagnostics;
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
        IOptions<AgentSettings> settings,
        ILogger<CommandExecutor> logger)
    {
        _broadcaster = broadcaster;
        _tracker     = tracker;
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
            perCallChannel: null, userName: userName, password: password));
        return (true, execId);
    }

    // ── Streamed RunCommand (new: returns a channel the caller reads) ──

    public (bool Accepted, string ExecutionId, ChannelReader<ExecutionEvent>? Stream) RunCommandStreamed(
        string command, string arguments, bool isReboot,
        string? executionId = null, string? userName = null, string? password = null)
    {
        if (_state == AgentState.Running)
            return (false, string.Empty, null);

        var execId = executionId ?? Guid.NewGuid().ToString("N")[..12];
        var ch = Channel.CreateUnbounded<ExecutionEvent>();

        _ = Task.Run(() => ExecuteAsync(execId, command, arguments, isReboot, ch.Writer,
            userName: userName, password: password));
        return (true, execId, ch.Reader);
    }

    // ── Core execution pipeline ────────────────────────────────────────

    private async Task ExecuteAsync(
        string executionId,
        string command,
        string arguments,
        bool isReboot,
        ChannelWriter<ExecutionEvent>? perCallChannel,
        string? userName = null,
        string? password = null)
    {
        await _executionLock.WaitAsync();

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
                psi.UserName = userName;
                if (!string.IsNullOrEmpty(password))
                {
                    var securePassword = new System.Security.SecureString();
                    foreach (char c in password) securePassword.AppendChar(c);
                    securePassword.MakeReadOnly();
                    psi.Password = securePassword;
                }
                _logger.LogInformation("Running as user: {User}", userName);
            }

            _currentProcess = Process.Start(psi);
            if (_currentProcess is null)
            {
                throw new InvalidOperationException("Process.Start returned null");
            }

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

            // ── WAIT FOR EXIT ──────────────────────────────────────
            await _currentProcess.WaitForExitAsync();

            // Ensure streams are fully drained after process exits
            await Task.WhenAll(stdoutTask, stderrTask);

            _lastExitCode = _currentProcess.ExitCode;
            record.Complete(_lastExitCode);

            // ── COMPLETED ──────────────────────────────────────────
            EmitEvent(executionId, ExecutionEventType.EventCompleted,
                exitCode: _lastExitCode,
                detail: $"Exit code {_lastExitCode}",
                perCallChannel: perCallChannel);

            _logger.LogInformation("Execution {Id} completed — exit {Code}",
                executionId, _lastExitCode);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            record.Fail(ex.Message);

            EmitEvent(executionId, ExecutionEventType.EventFailed,
                errorMessage: ex.Message,
                detail: $"Execution failed: {ex.Message}",
                perCallChannel: perCallChannel);

            _logger.LogError(ex, "Execution {Id} failed", executionId);
        }
        finally
        {
            if (!isReboot)
            {
                _currentProcess?.Dispose();
                _currentProcess = null;
                _currentExecutionId = null;
                _currentCommand = null;
                _executionStartedUtc = null;

                SetActivity($"Finished: {command} {arguments}");
                SetState(AgentState.Ready);
            }

            perCallChannel?.TryComplete();
            _executionLock.Release();
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
            if (_currentProcess is { HasExited: false })
            {
                var execId = _currentExecutionId ?? "unknown";
                _logger.LogWarning("Terminating PID {Pid}", _currentProcess.Id);
                _currentProcess.Kill(entireProcessTree: true);

                EmitEvent(execId, ExecutionEventType.EventTerminated,
                    detail: "Terminated by controller request");

                _tracker.GetCurrent()?.Terminate();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TerminateExecution failed");
            _lastError = ex.Message;
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
        ChannelWriter<ExecutionEvent>? perCallChannel = null)
    {
        var evt = BuildEvent(executionId, type, outputLine, outputKind,
            exitCode, errorMessage, agentState, command, arguments, detail);

        _broadcaster.Publish(evt);
        perCallChannel?.TryWrite(evt);
    }

    private ExecutionEvent BuildEvent(string executionId, ExecutionEventType type,
        string? outputLine = null, OutputKind? outputKind = null,
        int? exitCode = null, string? errorMessage = null,
        AgentState? agentState = null, string? command = null,
        string? arguments = null, string? detail = null)
    {
        var evt = new ExecutionEvent
        {
            ExecutionId = executionId,
            AgentName   = _settings.GetResolvedEndpoint(),
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
        return evt;
    }

    public void Dispose()
    {
        _currentProcess?.Dispose();
        _executionLock.Dispose();
    }
}
