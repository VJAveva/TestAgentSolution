using System.Collections.Concurrent;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Options;

namespace TestAgentGrpc.Services;

/// <summary>
/// Tracks all command executions with full audit trail: command, arguments,
/// timing, stdout/stderr capture, exit code, and outcome.
///
/// This is an entirely new capability — the legacy agent only stored the
/// last exit code and last error string.
/// </summary>
public sealed class ExecutionTracker
{
    private readonly int _maxHistory;
    private readonly int _maxLines;
    private readonly ConcurrentQueue<ExecutionRecord> _history = new();
    private ExecutionRecordBuilder? _current;
    private int _completedCount;
    private int _failedCount;

    public int CompletedCount => _completedCount;
    public int FailedCount    => _failedCount;

    public ExecutionTracker(IOptions<AgentSettings> settings)
    {
        _maxHistory = settings.Value.MaxExecutionHistoryCount;
        _maxLines   = settings.Value.MaxOutputLinesPerExecution;
    }

    public ExecutionRecordBuilder Begin(string executionId, string command, string arguments)
    {
        _current = new ExecutionRecordBuilder(executionId, command, arguments, _maxLines);
        return _current;
    }

    public ExecutionRecordBuilder? GetCurrent() => _current;

    public IReadOnlyCollection<ExecutionRecord> GetHistory(int max = 0, string? filterCommand = null)
    {
        IEnumerable<ExecutionRecord> query = _history.Reverse(); // most recent first

        if (!string.IsNullOrEmpty(filterCommand))
            query = query.Where(r =>
                r.Command.Contains(filterCommand, StringComparison.OrdinalIgnoreCase));

        if (max > 0)
            query = query.Take(max);

        return query.ToList();
    }

    internal void Archive(ExecutionRecord record)
    {
        _history.Enqueue(record);

        // Trim ring buffer
        while (_history.Count > _maxHistory)
            _history.TryDequeue(out _);

        if (record.Outcome == ExecutionOutcome.OutcomeSuccess)
            Interlocked.Increment(ref _completedCount);
        else
            Interlocked.Increment(ref _failedCount);

        _current = null;
    }

    // ── Builder accumulates data during execution ──────────────────────

    public sealed class ExecutionRecordBuilder
    {
        private readonly string _executionId;
        private readonly string _command;
        private readonly string _arguments;
        private readonly DateTime _startedUtc;
        private readonly int _maxLines;
        private readonly List<string> _stdout = new();
        private readonly List<string> _stderr = new();
        private readonly object _lock = new();

        private DateTime? _finishedUtc;
        private int _exitCode;
        private string _errorMessage = string.Empty;
        private ExecutionOutcome _outcome = ExecutionOutcome.OutcomeUnknown;

        internal ExecutionRecordBuilder(string id, string cmd, string args, int maxLines)
        {
            _executionId = id;
            _command     = cmd;
            _arguments   = args;
            _startedUtc  = DateTime.UtcNow;
            _maxLines    = maxLines;
        }

        public void AddOutputLine(OutputKind kind, string line)
        {
            lock (_lock)
            {
                var target = kind == OutputKind.OutputStdout ? _stdout : _stderr;
                if (target.Count < _maxLines)
                    target.Add(line);
            }
        }

        public void Complete(int exitCode)
        {
            _finishedUtc = DateTime.UtcNow;
            _exitCode = exitCode;
            _outcome = exitCode == 0 ? ExecutionOutcome.OutcomeSuccess : ExecutionOutcome.OutcomeFailed;
            Archive();
        }

        public void Fail(string error)
        {
            _finishedUtc = DateTime.UtcNow;
            _errorMessage = error;
            _outcome = ExecutionOutcome.OutcomeFailed;
            Archive();
        }

        public void Terminate()
        {
            _finishedUtc = DateTime.UtcNow;
            _outcome = ExecutionOutcome.OutcomeTerminated;
            Archive();
        }

        private void Archive()
        {
            // Build proto record
            var record = new ExecutionRecord
            {
                ExecutionId  = _executionId,
                Command      = _command,
                Arguments    = _arguments,
                Started      = Timestamp.FromDateTime(_startedUtc),
                Finished     = Timestamp.FromDateTime(_finishedUtc ?? DateTime.UtcNow),
                ExitCode     = _exitCode,
                ErrorMessage = _errorMessage,
                Outcome      = _outcome,
            };
            lock (_lock)
            {
                record.StdoutLines.AddRange(_stdout);
                record.StderrLines.AddRange(_stderr);
            }

            // Walk up to the parent tracker via a static callback
            _onArchive?.Invoke(record);
        }

        // Wired up by ExecutionTracker.Begin
        internal Action<ExecutionRecord>? _onArchive;
    }

    // Wire builder → tracker archiving
    public ExecutionRecordBuilder BeginTracked(string executionId, string command, string arguments)
    {
        var builder = Begin(executionId, command, arguments);
        builder._onArchive = Archive;
        return builder;
    }
}
