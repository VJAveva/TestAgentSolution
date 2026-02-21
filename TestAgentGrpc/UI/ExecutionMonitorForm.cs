using System.Drawing;
using TestAgentGrpc.Services;

namespace TestAgentGrpc.UI;

/// <summary>
/// WinForms window that provides a real-time execution monitor for the agent.
///
/// Layout:
/// ┌──────────────────────────────────────────────────────────────────┐
/// │ [State: Ready] [CPU: 12%] [Mem: 234MB] [Disk: 45GB] [Runs: 15] │ ← Status bar
/// ├──────────────────────────────────────────────────────────────────┤
/// │ ┌── Live Output ──────────────────────────────────────────────┐  │
/// │ │ > cmd.exe /c build.bat                                      │  │
/// │ │ [STDOUT] Compiling module A...                              │  │
/// │ │ [STDOUT] Compiling module B...                              │  │
/// │ │ [STDERR] Warning CS0168: unused variable                    │  │
/// │ │ [COMPLETED] Exit code 0                                     │  │
/// │ └────────────────────────────────────────────────────────────┘  │
/// ├──────────────────────────────────────────────────────────────────┤
/// │ Execution History        | Command          | Exit | Duration   │ ← DataGridView
/// │ abc123  2024-01-15 10:30 | build.bat        |  0   | 00:02:31   │
/// │ def456  2024-01-15 09:15 | test_runner.exe  | -1   | 00:15:44   │
/// └──────────────────────────────────────────────────────────────────┘
///
/// Entirely new — the legacy agent only had a flat text log in a WPF window.
/// </summary>
public sealed class ExecutionMonitorForm : Form
{
    private readonly CommandExecutor _executor;
    private readonly EventBroadcaster _broadcaster;
    private readonly ExecutionTracker _tracker;
    private readonly SystemMetricsCollector _metrics;

    // Controls
    private readonly StatusStrip _statusStrip;
    private readonly ToolStripStatusLabel _stateLabel;
    private readonly ToolStripStatusLabel _cpuLabel;
    private readonly ToolStripStatusLabel _memLabel;
    private readonly ToolStripStatusLabel _diskLabel;
    private readonly ToolStripStatusLabel _runsLabel;
    private readonly RichTextBox _liveOutput;
    private readonly DataGridView _historyGrid;
    private readonly SplitContainer _splitter;

    private IDisposable? _subscription;
    private CancellationTokenSource? _cts;

    public ExecutionMonitorForm(
        CommandExecutor executor,
        EventBroadcaster broadcaster,
        ExecutionTracker tracker,
        SystemMetricsCollector metrics)
    {
        _executor    = executor;
        _broadcaster = broadcaster;
        _tracker     = tracker;
        _metrics     = metrics;

        // ── Form setup ─────────────────────────────────────────────
        Text = "TestAgent — Execution Monitor";
        Width = 900;
        Height = 650;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        // ── Status strip ───────────────────────────────────────────
        _stateLabel = new ToolStripStatusLabel("State: —") { Spring = false };
        _cpuLabel   = new ToolStripStatusLabel("CPU: —") { BorderSides = ToolStripStatusLabelBorderSides.Left };
        _memLabel   = new ToolStripStatusLabel("Mem: —") { BorderSides = ToolStripStatusLabelBorderSides.Left };
        _diskLabel  = new ToolStripStatusLabel("Disk: —") { BorderSides = ToolStripStatusLabelBorderSides.Left };
        _runsLabel  = new ToolStripStatusLabel("Runs: 0/0") { BorderSides = ToolStripStatusLabelBorderSides.Left, Spring = true };

        _statusStrip = new StatusStrip();
        _statusStrip.Items.AddRange(new ToolStripItem[] { _stateLabel, _cpuLabel, _memLabel, _diskLabel, _runsLabel });

        // ── Live output (top panel) ────────────────────────────────
        var liveLabel = new Label
        {
            Text = "  ▶ Live Execution Output",
            Dock = DockStyle.Top,
            Height = 24,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.FromArgb(0, 200, 100),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _liveOutput = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(20, 20, 20),
            ForeColor = Color.FromArgb(204, 204, 204),
            Font = new Font("Cascadia Mono", 9.5f, FontStyle.Regular, GraphicsUnit.Point),
            WordWrap = false,
            ScrollBars = RichTextBoxScrollBars.Both,
        };

        var topPanel = new Panel { Dock = DockStyle.Fill };
        topPanel.Controls.Add(_liveOutput);
        topPanel.Controls.Add(liveLabel);

        // ── History grid (bottom panel) ────────────────────────────
        var histLabel = new Label
        {
            Text = "  📋 Execution History",
            Dock = DockStyle.Top,
            Height = 24,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            BackColor = Color.FromArgb(40, 40, 40),
            ForeColor = Color.White,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _historyGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            BackgroundColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.White,
            GridColor = Color.FromArgb(60, 60, 60),
            DefaultCellStyle = { BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.White },
            ColumnHeadersDefaultCellStyle = { BackColor = Color.FromArgb(50, 50, 50), ForeColor = Color.White },
            EnableHeadersVisualStyles = false,
        };

        _historyGrid.Columns.Add("Id", "Execution ID");
        _historyGrid.Columns.Add("Command", "Command");
        _historyGrid.Columns.Add("CommandText", "Command Text");
        _historyGrid.Columns.Add("Operation", "Operation");
        _historyGrid.Columns.Add("Outcome", "Outcome");
        _historyGrid.Columns.Add("ExitCode", "Exit");
        _historyGrid.Columns.Add("Duration", "Duration");
        _historyGrid.Columns.Add("Started", "Started");
        _historyGrid.Columns["Id"]!.FillWeight = 12;
        _historyGrid.Columns["Command"]!.FillWeight = 18;
        _historyGrid.Columns["CommandText"]!.FillWeight = 22;
        _historyGrid.Columns["Operation"]!.FillWeight = 14;
        _historyGrid.Columns["Outcome"]!.FillWeight = 10;
        _historyGrid.Columns["ExitCode"]!.FillWeight = 6;
        _historyGrid.Columns["Duration"]!.FillWeight = 10;
        _historyGrid.Columns["Started"]!.FillWeight = 14;

        var bottomPanel = new Panel { Dock = DockStyle.Fill };
        bottomPanel.Controls.Add(_historyGrid);
        bottomPanel.Controls.Add(histLabel);

        // ── Splitter ───────────────────────────────────────────────
        _splitter = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 350,
            BackColor = Color.FromArgb(60, 60, 60),
        };
        _splitter.Panel1.Controls.Add(topPanel);
        _splitter.Panel2.Controls.Add(bottomPanel);

        Controls.Add(_splitter);
        Controls.Add(_statusStrip);

        // ── Start event consumption ────────────────────────────────
        Load += OnLoad;
        FormClosed += OnFormClosed;
    }

    private void OnLoad(object? s, EventArgs e)
    {
        _cts = new CancellationTokenSource();
        var (reader, sub) = _broadcaster.Subscribe();
        _subscription = sub;

        // Consume events on a background task, marshal to UI thread
        _ = Task.Run(async () =>
        {
            await foreach (var evt in reader.ReadAllAsync(_cts.Token))
            {
                try { Invoke(() => HandleEvent(evt)); } catch { }
            }
        });

        // Refresh metrics periodically
        _ = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                await Task.Delay(3000, _cts.Token);
                try { Invoke(RefreshStatus); } catch { }
            }
        });

        // Load existing history
        RefreshHistory();
        RefreshStatus();
    }

    private void OnFormClosed(object? s, FormClosedEventArgs e)
    {
        _cts?.Cancel();
        _subscription?.Dispose();
    }

    // ── Event handler ──────────────────────────────────────────────────

    private void HandleEvent(ExecutionEvent evt)
    {
        switch (evt.EventType)
        {
            case ExecutionEventType.EventQueued:
                AppendLine($"⏳ [{evt.ExecutionId}] QUEUED: {evt.Detail}", Color.Gray);
                break;

            case ExecutionEventType.EventStarted:
                AppendLine($"🚀 [{evt.ExecutionId}] STARTED: {evt.Command} {evt.Arguments}", Color.FromArgb(100, 200, 255));
                AppendLine($"   Execution ID: {evt.ExecutionId}", Color.FromArgb(150, 150, 255));
                break;

            case ExecutionEventType.EventStdoutLine:
                AppendLine($"   {evt.OutputLine}", Color.FromArgb(204, 204, 204));
                break;

            case ExecutionEventType.EventStderrLine:
                AppendLine($"⚠  {evt.OutputLine}", Color.FromArgb(255, 180, 80));
                break;

            case ExecutionEventType.EventCompleted:
                var color = evt.ExitCode == 0 ? Color.FromArgb(80, 220, 100) : Color.FromArgb(255, 100, 100);
                AppendLine($"✅ [{evt.ExecutionId}] COMPLETED — exit code {evt.ExitCode}", color);
                AppendLine("", Color.Gray); // blank separator
                RefreshHistory();
                break;

            case ExecutionEventType.EventFailed:
                AppendLine($"❌ [{evt.ExecutionId}] FAILED: {evt.ErrorMessage}", Color.Red);
                AppendLine("", Color.Gray);
                RefreshHistory();
                break;

            case ExecutionEventType.EventTerminated:
                AppendLine($"🛑 [{evt.ExecutionId}] TERMINATED: {evt.Detail}", Color.FromArgb(255, 100, 100));
                RefreshHistory();
                break;

            case ExecutionEventType.EventStateChanged:
                RefreshStatus();
                break;

            case ExecutionEventType.EventHeartbeat:
                RefreshStatus();
                break;
        }
    }

    private void AppendLine(string text, Color color)
    {
        if (_liveOutput.IsDisposed) return;

        _liveOutput.SelectionStart = _liveOutput.TextLength;
        _liveOutput.SelectionLength = 0;
        _liveOutput.SelectionColor = color;
        _liveOutput.AppendText(text + Environment.NewLine);
        _liveOutput.ScrollToCaret();

        // Cap output buffer
        if (_liveOutput.Lines.Length > 10000)
        {
            _liveOutput.SelectionStart = 0;
            _liveOutput.SelectionLength = _liveOutput.GetFirstCharIndexFromLine(2000);
            _liveOutput.SelectedText = "[...truncated...]\n";
        }
    }

    private void RefreshStatus()
    {
        var state = _executor.CurrentState switch
        {
            AgentState.Ready   => "● Ready",
            AgentState.Running => "▶ Running",
            _                  => "○ Inactive",
        };
        _stateLabel.Text = $"State: {state}";

        var m = _metrics.Collect();
        _cpuLabel.Text  = $"CPU: {m.CpuUsagePct}%";
        _memLabel.Text  = $"Mem: {m.MemoryUsedMb}MB";
        _diskLabel.Text = $"Disk: {m.DiskFreeGb}GB free";
        _runsLabel.Text = $"✓ {_tracker.CompletedCount}  ✗ {_tracker.FailedCount}";
    }

    private void RefreshHistory()
    {
        _historyGrid.Rows.Clear();
        foreach (var r in _tracker.GetHistory(50))
        {
            var started  = r.Started?.ToDateTime().ToLocalTime().ToString("HH:mm:ss dd-MMM") ?? "—";
            var duration = (r.Finished is not null && r.Started is not null)
                ? (r.Finished.ToDateTime() - r.Started.ToDateTime()).ToString(@"hh\:mm\:ss")
                : "—";
            var outcome = r.Outcome switch
            {
                ExecutionOutcome.OutcomeSuccess    => "✓ Success",
                ExecutionOutcome.OutcomeFailed     => "✗ Failed",
                ExecutionOutcome.OutcomeTerminated => "⊘ Terminated",
                ExecutionOutcome.OutcomeTimedOut   => "⏱ Timeout",
                _                                  => "?",
            };

            // Command Text: the full command + arguments
            var commandText = $"{r.Command} {r.Arguments}".Trim();

            // Operation: infer from command/arguments
            var operation = InferOperation(r.Command, r.Arguments);

            _historyGrid.Rows.Add(r.ExecutionId, r.Command, commandText, operation, outcome, r.ExitCode, duration, started);
        }
    }

    /// <summary>
    /// Infers a human-readable operation description from the command and arguments.
    /// </summary>
    private static string InferOperation(string command, string arguments)
    {
        var cmd = command.Trim().Trim('"').ToLowerInvariant();
        var args = arguments.ToLowerInvariant();

        // By extension
        var ext = "";
        try { ext = Path.GetExtension(cmd); } catch { }

        if (cmd.Contains("build") || args.Contains("build"))
            return "Build";
        if (cmd.Contains("deploy") || args.Contains("deploy"))
            return "Deploy";
        if (cmd.Contains("test") || args.Contains("test") || cmd.Contains("smoke"))
            return "Test";
        if (cmd.Contains("install") || args.Contains("install") || ext == ".msi")
            return "Install";
        if (cmd.Contains("setup") || args.Contains("setup"))
            return "Setup";
        if (cmd == "shutdown" || cmd == "restart" || args.Contains("/r"))
            return "Reboot";
        if (cmd.Contains("copy") || args.Contains("xcopy") || args.Contains("robocopy"))
            return "FileCopy";
        if (ext == ".ps1")
            return "PowerShell";
        if (ext is ".bat" or ".cmd")
            return "Script";
        if (cmd is "cmd" or "cmd.exe")
            return "Command";
        if (cmd is "powershell" or "powershell.exe" or "pwsh" or "pwsh.exe")
            return "PowerShell";

        return "Execute";
    }
}
