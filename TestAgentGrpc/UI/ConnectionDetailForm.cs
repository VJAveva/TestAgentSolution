using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using TestAgentGrpc.Clients;
using TestAgentGrpc.Services;

namespace TestAgentGrpc.UI;

/// <summary>
/// Full communication diagnostics form opened from the system tray context menu.
/// Dark theme matching ExecutionMonitorForm.
/// </summary>
public sealed class ConnectionDetailForm : Form
{
    // ?? Theme constants ????????????????????????????????????????????????
    private static readonly Color BgDark  = Color.FromArgb(30, 30, 46);    // #1E1E2E
    private static readonly Color BgPanel = Color.FromArgb(24, 24, 37);
    private static readonly Color BgCard  = Color.FromArgb(40, 40, 58);
    private static readonly Color TextPrimary   = Color.FromArgb(205, 214, 244);  // #CDD6F4
    private static readonly Color TextSecondary = Color.FromArgb(144, 164, 174);
    private static readonly Color AccentGreen  = Color.FromArgb(0, 200, 100);
    private static readonly Color AccentYellow = Color.FromArgb(240, 200, 0);
    private static readonly Color AccentRed    = Color.FromArgb(220, 60, 60);
    private static readonly Font FontLabel   = new("Segoe UI", 9f);
    private static readonly Font FontMono    = new("Consolas", 9f);
    private static readonly Font FontHeading = new("Segoe UI", 9.5f, FontStyle.Bold);

    // ?? Dependencies ???????????????????????????????????????????????????
    private readonly ConnectionHealthMonitor _monitor;
    private readonly AgentSettings _agentSettings;
    private readonly TestControllerClient _controllerClient;
    private readonly SystemMetricsCollector _metrics;

    // ?? Controls ???????????????????????????????????????????????????????
    private readonly Label _controllerStatusLabel;
    private readonly Label _controllerAddressLabel;
    private readonly Label _registrationLabel;
    private readonly Label _heartbeatStatsLabel;
    private readonly Label _agentEndpointLabel;
    private readonly Label _agentNameLabel;
    private readonly Label _agentCallbackLabel;
    private readonly DataGridView _logGrid;
    private readonly Button _diagButton;
    private readonly Button _copyButton;
    private readonly Button _exportButton;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    public ConnectionDetailForm(
        ConnectionHealthMonitor monitor,
        AgentSettings agentSettings,
        TestControllerClient controllerClient,
        SystemMetricsCollector metrics)
    {
        _monitor          = monitor;
        _agentSettings    = agentSettings;
        _controllerClient = controllerClient;
        _metrics          = metrics;

        // ?? Form ???????????????????????????????????????????????????
        Text = "Connection Details";
        Width = 560;
        Height = 640;
        MinimumSize = new Size(480, 500);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = BgDark;
        ForeColor = TextPrimary;
        Font = FontLabel;

        var mainPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        Controls.Add(mainPanel);

        int y = 8;

        // ?? Controller Connection panel ????????????????????????????
        var ctrlGroup = CreateGroupBox("Controller Connection", ref y);
        mainPanel.Controls.Add(ctrlGroup);

        int gy = 20;
        _controllerStatusLabel  = AddLabelPair(ctrlGroup, "Status:", ref gy);
        _controllerAddressLabel = AddLabelPair(ctrlGroup, "Address:", ref gy);
        _registrationLabel      = AddLabelPair(ctrlGroup, "Registered:", ref gy);
        _heartbeatStatsLabel    = AddLabelPair(ctrlGroup, "Heartbeats:", ref gy);
        ctrlGroup.Height = gy + 8;
        y += ctrlGroup.Height + 8;

        // ?? Agent Endpoint panel ???????????????????????????????????
        var agentGroup = CreateGroupBox("Agent Endpoint", ref y);
        mainPanel.Controls.Add(agentGroup);

        int ay = 20;
        _agentEndpointLabel = AddLabelPair(agentGroup, "Listen:", ref ay);
        _agentNameLabel     = AddLabelPair(agentGroup, "Name:", ref ay);
        _agentCallbackLabel = AddLabelPair(agentGroup, "Callback:", ref ay);
        agentGroup.Height = ay + 8;
        y += agentGroup.Height + 8;

        // ?? Communication Log ??????????????????????????????????????
        var logLabel = new Label
        {
            Text = "Recent Communication Log",
            Font = FontHeading,
            ForeColor = TextPrimary,
            Location = new Point(4, y),
            AutoSize = true,
        };
        mainPanel.Controls.Add(logLabel);
        y += 22;

        _logGrid = new DataGridView
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            Location = new Point(4, y),
            Size = new Size(520, 240),
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = BgPanel,
            GridColor = Color.FromArgb(50, 50, 70),
            BorderStyle = BorderStyle.None,
            Font = FontMono,
            ForeColor = TextPrimary,
            DefaultCellStyle = { BackColor = BgPanel, ForeColor = TextPrimary, SelectionBackColor = BgCard },
            ColumnHeadersDefaultCellStyle = { BackColor = BgCard, ForeColor = TextPrimary, Font = FontLabel },
            EnableHeadersVisualStyles = false,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        };
        _logGrid.Columns.Add("Time", "Time");
        _logGrid.Columns.Add("Sev", "");
        _logGrid.Columns.Add("Event", "Event");
        _logGrid.Columns.Add("Detail", "Detail");
        _logGrid.Columns["Time"]!.FillWeight   = 15;
        _logGrid.Columns["Sev"]!.FillWeight    = 5;
        _logGrid.Columns["Event"]!.FillWeight  = 18;
        _logGrid.Columns["Detail"]!.FillWeight = 62;
        mainPanel.Controls.Add(_logGrid);

        // ?? Footer buttons ?????????????????????????????????????????
        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 40,
            Padding = new Padding(4),
            BackColor = BgDark,
        };
        Controls.Add(buttonPanel);

        var closeButton = CreateButton("Close");
        closeButton.Click += (_, _) => Close();
        buttonPanel.Controls.Add(closeButton);

        _exportButton = CreateButton("Export CSV");
        _exportButton.Click += OnExportCsv;
        buttonPanel.Controls.Add(_exportButton);

        _copyButton = CreateButton("Copy to Clipboard");
        _copyButton.Click += OnCopyToClipboard;
        buttonPanel.Controls.Add(_copyButton);

        _diagButton = CreateButton("Run Self-Diagnostic");
        _diagButton.Click += OnRunDiagnostic;
        buttonPanel.Controls.Add(_diagButton);

        // ?? Refresh timer ??????????????????????????????????????????
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _refreshTimer.Tick += (_, _) => RefreshAll();
        _refreshTimer.Start();

        RefreshAll();
    }

    // ?? Refresh ????????????????????????????????????????????????????????

    private void RefreshAll()
    {
        try
        {
            // Controller panel
            var status = _monitor.GetStatus();
            var statusIcon = status switch
            {
                ConnectionStatus.Connected    => "? Connected",
                ConnectionStatus.Warning      => "? Warning",
                ConnectionStatus.Disconnected => "? Disconnected",
                _                             => "? Not Registered",
            };
            _controllerStatusLabel.Text = statusIcon;
            _controllerStatusLabel.ForeColor = status switch
            {
                ConnectionStatus.Connected    => AccentGreen,
                ConnectionStatus.Warning      => AccentYellow,
                ConnectionStatus.Disconnected => AccentRed,
                _                             => TextSecondary,
            };

            _controllerAddressLabel.Text = _monitor.ControllerAddress ?? "—";
            _registrationLabel.Text = _monitor.RegistrationTimestamp?.ToLocalTime().ToString("g") ?? "Never";

            var uptime = _monitor.UptimeSinceLastRecovery;
            var uptimeStr = uptime.TotalHours >= 1 ? uptime.ToString(@"hh\:mm\:ss") : uptime.ToString(@"mm\:ss");
            _heartbeatStatsLabel.Text =
                $"{_monitor.TotalHeartbeatsSent} OK / {_monitor.TotalHeartbeatsFailed} Failed / " +
                $"Streak: {_monitor.CurrentSuccessStreak} / Up: {uptimeStr}";

            // Agent panel
            _agentEndpointLabel.Text = $"0.0.0.0:{_agentSettings.GrpcPort}";
            _agentNameLabel.Text = _agentSettings.AgentName;
            _agentCallbackLabel.Text = _agentSettings.GetResolvedEndpoint();

            // Log grid
            RefreshLogGrid();
        }
        catch { }
    }

    private void RefreshLogGrid()
    {
        var entries = _monitor.GetRecentLog();
        _logGrid.Rows.Clear();

        foreach (var e in entries)
        {
            var timeStr = e.Timestamp.ToLocalTime().ToString("HH:mm:ss");
            var icon = e.Severity switch
            {
                CommunicationSeverity.Success => "?",
                CommunicationSeverity.Warning => "?",
                CommunicationSeverity.Error   => "?",
                _                             => "·",
            };
            var detail = e.Detail.Length > 120 ? $"{e.Detail[..117]}…" : e.Detail;
            var rowIdx = _logGrid.Rows.Add(timeStr, icon, e.Event, detail);

            var rowColor = e.Severity switch
            {
                CommunicationSeverity.Success => Color.FromArgb(30, 50, 30),
                CommunicationSeverity.Warning => Color.FromArgb(50, 45, 20),
                CommunicationSeverity.Error   => Color.FromArgb(50, 25, 25),
                _                             => BgPanel,
            };
            _logGrid.Rows[rowIdx].DefaultCellStyle.BackColor = rowColor;
        }

        // Auto-scroll to top (most recent)
        if (_logGrid.Rows.Count > 0)
            _logGrid.FirstDisplayedScrollingRowIndex = 0;
    }

    // ?? Self-diagnostic ????????????????????????????????????????????????

    private async void OnRunDiagnostic(object? sender, EventArgs e)
    {
        _diagButton.Enabled = false;
        _diagButton.Text = "Running…";
        var results = new List<string>();

        try
        {
            var address = _monitor.ControllerAddress ?? _agentSettings.ControllerAddress;
            var uri = new Uri(address);
            var host = uri.Host;
            var port = uri.Port;

            // 1. DNS Resolve
            try
            {
                var sw = Stopwatch.StartNew();
                var addresses = await Dns.GetHostAddressesAsync(host);
                sw.Stop();
                results.Add($"? DNS Resolve: {host} ? {string.Join(", ", addresses.Select(a => a.ToString()))} ({sw.ElapsedMilliseconds}ms)");
            }
            catch (Exception ex)
            {
                results.Add($"? DNS Resolve: {ex.Message}");
            }

            // 2. TCP Connect
            try
            {
                using var tcp = new TcpClient();
                var sw = Stopwatch.StartNew();
                var connectTask = tcp.ConnectAsync(host, port);
                if (await Task.WhenAny(connectTask, Task.Delay(3000)) == connectTask)
                {
                    await connectTask; // propagate exceptions
                    sw.Stop();
                    results.Add($"? TCP Connect: {host}:{port} ({sw.ElapsedMilliseconds}ms)");
                }
                else
                {
                    results.Add($"? TCP Connect: Timeout after 3000ms");
                }
            }
            catch (Exception ex)
            {
                results.Add($"? TCP Connect: {ex.Message}");
            }

            // 3. gRPC Heartbeat ping
            try
            {
                var sw = Stopwatch.StartNew();
                await _controllerClient.SendHeartbeatAsync(
                    AgentState.Ready, _metrics.Collect(), CancellationToken.None);
                sw.Stop();
                results.Add($"? gRPC Heartbeat: {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                results.Add($"? gRPC Heartbeat: {ex.Message}");
            }

            // 4. Latency (3 round-trips)
            try
            {
                var latencies = new List<long>();
                for (int i = 0; i < 3; i++)
                {
                    var sw = Stopwatch.StartNew();
                    await _controllerClient.SendHeartbeatAsync(
                        AgentState.Ready, _metrics.Collect(), CancellationToken.None);
                    sw.Stop();
                    latencies.Add(sw.ElapsedMilliseconds);
                }
                var avg = latencies.Average();
                results.Add($"? Latency: avg {avg:F0}ms ({string.Join(", ", latencies.Select(l => $"{l}ms"))})");
            }
            catch (Exception ex)
            {
                results.Add($"? Latency: {ex.Message}");
            }

            // 5. Connection status
            results.Add(_monitor.IsConnected
                ? $"? Connection Status: Connected (streak {_monitor.CurrentSuccessStreak})"
                : $"? Connection Status: {_monitor.GetStatus()} ({_monitor.ConsecutiveFailures} failures)");
        }
        catch (Exception ex)
        {
            results.Add($"? Diagnostic error: {ex.Message}");
        }
        finally
        {
            _diagButton.Enabled = true;
            _diagButton.Text = "Run Self-Diagnostic";
        }

        MessageBox.Show(
            string.Join(Environment.NewLine, results),
            "Self-Diagnostic Results",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    // ?? Copy to clipboard ??????????????????????????????????????????????

    private void OnCopyToClipboard(object? sender, EventArgs e)
    {
        var uptime = _monitor.UptimeSinceLastRecovery;
        var uptimeStr = uptime.TotalHours >= 1
            ? $"{(int)uptime.TotalHours}h {uptime.Minutes:D2}m"
            : $"{uptime.Minutes}m {uptime.Seconds:D2}s";

        var lines = new[]
        {
            $"Agent: {_agentSettings.AgentName} ({_agentSettings.GetResolvedEndpoint()})",
            $"Controller: {_monitor.ControllerName ?? "—"} ({_monitor.ControllerAddress ?? "—"})",
            $"Status: {_monitor.GetStatus()} | Registered: {(_monitor.RegistrationTimestamp?.ToLocalTime().ToString("g") ?? "Never")} | Uptime: {uptimeStr}",
            $"Heartbeats: {_monitor.TotalHeartbeatsSent} OK / {_monitor.TotalHeartbeatsFailed} Failed / Current streak: {_monitor.CurrentSuccessStreak}",
        };

        Clipboard.SetText(string.Join(Environment.NewLine, lines));
        MessageBox.Show("Copied to clipboard.", "Connection Details", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ?? Export CSV ?????????????????????????????????????????????????????

    private void OnExportCsv(object? sender, EventArgs e)
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "CSV files|*.csv",
            FileName = $"comm_log_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        try
        {
            var entries = _monitor.GetRecentLog();
            using var writer = new StreamWriter(dlg.FileName);
            writer.WriteLine("Timestamp,Event,Severity,Detail");
            foreach (var entry in entries)
            {
                var detail = entry.Detail.Replace("\"", "\"\"");
                writer.WriteLine($"\"{entry.Timestamp:O}\",\"{entry.Event}\",\"{entry.Severity}\",\"{detail}\"");
            }
            MessageBox.Show($"Exported {entries.Count} entries.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Export failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ?? UI helpers ?????????????????????????????????????????????????????

    private GroupBox CreateGroupBox(string text, ref int y)
    {
        var gb = new GroupBox
        {
            Text = text,
            ForeColor = TextPrimary,
            Font = FontHeading,
            Location = new Point(4, y),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Width = 520,
        };
        return gb;
    }

    private Label AddLabelPair(Control parent, string caption, ref int y)
    {
        var lbl = new Label
        {
            Text = caption,
            Font = FontLabel,
            ForeColor = TextSecondary,
            Location = new Point(12, y),
            AutoSize = true,
        };
        parent.Controls.Add(lbl);

        var val = new Label
        {
            Text = "—",
            Font = FontMono,
            ForeColor = TextPrimary,
            Location = new Point(110, y),
            AutoSize = true,
        };
        parent.Controls.Add(val);

        y += 20;
        return val;
    }

    private static Button CreateButton(string text) => new()
    {
        Text = text,
        FlatStyle = FlatStyle.Flat,
        BackColor = BgCard,
        ForeColor = TextPrimary,
        Font = FontLabel,
        Padding = new Padding(8, 2, 8, 2),
        AutoSize = true,
        Margin = new Padding(4),
        Cursor = Cursors.Hand,
    };

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        base.OnFormClosed(e);
    }
}
