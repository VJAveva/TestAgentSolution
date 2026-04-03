using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestAgentDisplay.Services;
using TestAgentGrpc;

namespace TestAgentDisplay.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AgentConnectionManager _connectionManager;
    private readonly AuditTimelineViewModel _timeline;

    [ObservableProperty] private string _newAgentName = "";
    [ObservableProperty] private AgentNodeViewModel? _selectedAgent;
    [ObservableProperty] private string _statusMessage = "No agents connected.";
    [ObservableProperty] private string _commandText = "";
    [ObservableProperty] private string _commandArgs = "";

    public ObservableCollection<AgentNodeViewModel> Agents { get; } = new();
    public AuditTimelineViewModel Timeline => _timeline;

    public MainViewModel(AgentConnectionManager connectionManager, AuditTimelineViewModel timeline)
    {
        _connectionManager = connectionManager;
        _timeline = timeline;
        _connectionManager.EventReceived += OnEventReceived;
        _connectionManager.ConnectionStateChanged += OnConnectionChanged;
    }

    private const int DefaultGrpcPort = 5200;

    [RelayCommand]
    private async Task ConnectAgentAsync()
    {
        var nodeName = NewAgentName.Trim();
        if (string.IsNullOrEmpty(nodeName)) return;

        // Resolve the gRPC endpoint from the node name
        string addr;
        try
        {
            addr = ResolveAgentEndpoint(nodeName);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Unable to resolve agent endpoint for node '{nodeName}': {ex.Message}";
            return;
        }

        if (Agents.Any(a => a.Address == addr))
        {
            StatusMessage = $"Agent '{nodeName}' is already connected.";
            SelectedAgent = Agents.First(a => a.Address == addr);
            return;
        }

        var vm = new AgentNodeViewModel
        {
            Address = addr,
            DisplayName = nodeName,
        };
        Agents.Add(vm);
        SelectedAgent = vm;
        StatusMessage = $"Connecting to {nodeName} ({addr})…";

        try
        {
            await _connectionManager.ConnectAsync(addr);

            var snap = await _connectionManager.GetSnapshotAsync(addr);
            if (snap is not null)
                Application.Current?.Dispatcher.Invoke(() => vm.ApplySnapshot(snap));

            var hist = await _connectionManager.GetHistoryAsync(addr);
            if (hist is not null)
                Application.Current?.Dispatcher.Invoke(() => vm.ApplyHistory(hist));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Unable to resolve agent endpoint for node '{nodeName}': {ex.Message}";
            Agents.Remove(vm);
            SelectedAgent = Agents.FirstOrDefault();
        }
    }

    /// <summary>
    /// Resolves a gRPC endpoint address from a node name.
    /// If the input is already a full URI (http:// or https://), it is used as-is.
    /// Otherwise, the node name is treated as a hostname and combined with the default gRPC port.
    /// </summary>
    private static string ResolveAgentEndpoint(string nodeName)
    {
        // If user typed a full URI, use it directly
        if (nodeName.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            nodeName.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // Validate it parses as a URI
            _ = new Uri(nodeName);
            return nodeName;
        }

        // Treat as hostname — build the gRPC address
        return $"http://{nodeName}:{DefaultGrpcPort}";
    }

    [RelayCommand]
    private void DisconnectAgent()
    {
        if (SelectedAgent is null) return;
        _connectionManager.Disconnect(SelectedAgent.Address);
        Agents.Remove(SelectedAgent);
        SelectedAgent = Agents.FirstOrDefault();
        StatusMessage = $"{Agents.Count} agent(s) connected.";
    }

    [RelayCommand]
    private async Task RunCommandAsync()
    {
        if (SelectedAgent is null || string.IsNullOrWhiteSpace(CommandText)) return;
        StatusMessage = $"Sending command to {SelectedAgent.DisplayName}…";
        var reply = await _connectionManager.RunCommandAsync(
            SelectedAgent.Address, CommandText.Trim(), CommandArgs.Trim());
        StatusMessage = reply?.Accepted == true
            ? $"Command accepted (ID: {reply.ExecutionId})"
            : $"Command rejected: {reply?.Message ?? "agent unreachable"}";
    }

    [RelayCommand]
    private async Task TerminateExecutionAsync()
    {
        if (SelectedAgent is null) return;
        await _connectionManager.TerminateAsync(SelectedAgent.Address);
        StatusMessage = "Terminate signal sent.";
    }

    [RelayCommand]
    private async Task RefreshHistoryAsync()
    {
        if (SelectedAgent is null) return;
        var hist = await _connectionManager.GetHistoryAsync(SelectedAgent.Address);
        if (hist is not null)
            Application.Current?.Dispatcher.Invoke(() => SelectedAgent.ApplyHistory(hist));
    }

    [RelayCommand]
    private void ClearCommandInputs()
    {
        CommandText = "";
        CommandArgs = "";
    }

    [RelayCommand]
    private void ClearOutput()
    {
        SelectedAgent?.OutputLines.Clear();
    }

    [RelayCommand]
    private async Task ExportReportAsync()
    {
        // Use a SaveFileDialog from Win32 interop
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Multi-Agent Execution Report",
            Filter = "CSV files|*.csv|JSON files|*.json|HTML files|*.html",
            FileName = $"report_{DateTime.Now:yyyyMMdd_HHmmss}",
            FilterIndex = 3,
        };
        if (dlg.ShowDialog() != true) return;

        StatusMessage = "Exporting report…";

        try
        {
            var agentRecords = new Dictionary<string, IReadOnlyCollection<ExecutionRecord>>();

            foreach (var agent in Agents)
            {
                var hist = await _connectionManager.GetHistoryAsync(agent.Address, max: 200);
                if (hist is not null)
                    agentRecords[agent.DisplayName] = hist.Records;
            }

            if (agentRecords.Count == 0)
            {
                StatusMessage = "No data to export.";
                return;
            }

            var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
            var content = ext switch
            {
                ".csv"  => GenerateMultiAgentCsv(agentRecords),
                ".json" => GenerateMultiAgentJson(agentRecords),
                ".html" => GenerateMultiAgentHtml(agentRecords),
                _       => GenerateMultiAgentHtml(agentRecords),
            };

            await File.WriteAllTextAsync(dlg.FileName, content);
            var total = agentRecords.Values.Sum(r => r.Count);
            StatusMessage = $"Report exported: {total} executions from {agentRecords.Count} agent(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
        }
    }

    // ── Report generation (duplicated locally to avoid cross-project dependency) ──

    private static string GenerateMultiAgentCsv(Dictionary<string, IReadOnlyCollection<ExecutionRecord>> agentRecords)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Agent,Time,Command,Arguments,ExitCode,Duration,Outcome,StdoutLines,StderrLines");

        foreach (var (agentName, records) in agentRecords.OrderBy(kv => kv.Key))
        {
            foreach (var r in records)
            {
                var started = r.Started?.ToDateTime().ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "";
                var duration = (r.Finished is not null && r.Started is not null)
                    ? (r.Finished.ToDateTime() - r.Started.ToDateTime()).ToString(@"hh\:mm\:ss") : "";
                var outcome = FormatOutcome(r.Outcome);
                sb.AppendLine($"\"{Esc(agentName)}\",\"{started}\",\"{Esc(r.Command)}\",\"{Esc(r.Arguments)}\",{r.ExitCode},\"{duration}\",\"{outcome}\",{r.StdoutLines.Count},{r.StderrLines.Count}");
            }
        }
        return sb.ToString();
    }

    private static string GenerateMultiAgentJson(Dictionary<string, IReadOnlyCollection<ExecutionRecord>> agentRecords)
    {
        var totalAll = agentRecords.Values.Sum(r => r.Count);
        var successAll = agentRecords.Values.Sum(r => r.Count(e => e.Outcome == ExecutionOutcome.OutcomeSuccess));

        var report = new
        {
            GeneratedUtc = DateTime.UtcNow.ToString("O"),
            Summary = new { AgentCount = agentRecords.Count, TotalExecutions = totalAll, Succeeded = successAll, Failed = totalAll - successAll },
            Agents = agentRecords.OrderBy(kv => kv.Key).Select(kv => new
            {
                AgentName = kv.Key,
                Executions = kv.Value.Select(r => new
                {
                    r.ExecutionId, r.Command, r.Arguments,
                    Started = r.Started?.ToDateTime().ToString("O"),
                    Finished = r.Finished?.ToDateTime().ToString("O"),
                    r.ExitCode, Outcome = r.Outcome.ToString(),
                    StdoutLineCount = r.StdoutLines.Count, StderrLineCount = r.StderrLines.Count,
                }).ToArray(),
            }).ToArray(),
        };

        return JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
    }

    private static string GenerateMultiAgentHtml(Dictionary<string, IReadOnlyCollection<ExecutionRecord>> agentRecords)
    {
        var sb = new StringBuilder();
        var totalAll = agentRecords.Values.Sum(r => r.Count);
        var successAll = agentRecords.Values.Sum(r => r.Count(e => e.Outcome == ExecutionOutcome.OutcomeSuccess));
        var failedAll = totalAll - successAll;

        sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'/><title>Multi-Agent Execution Report</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("""
            body { background:#1a1a2e; color:#e0e0e0; font-family:'Segoe UI',sans-serif; margin:20px; }
            h1,h2 { color:#00c9a7; } h2 { margin-top:32px; border-bottom:1px solid #0f3460; padding-bottom:8px; }
            .cards { display:flex; gap:16px; margin:16px 0; }
            .card { background:#16213e; border-radius:8px; padding:16px 24px; min-width:120px; }
            .card .num { font-size:28px; font-weight:bold; color:#00c9a7; }
            .card .lbl { font-size:12px; color:#90a4ae; }
            .fail .num { color:#ef5350; }
            table { border-collapse:collapse; width:100%; margin-top:8px; }
            th { background:#0f3460; color:#90a4ae; text-align:left; padding:8px 12px; font-size:12px; }
            td { padding:6px 12px; border-bottom:1px solid #16213e; font-size:12px; }
            tr:hover { background:#16213e; }
            .ok { color:#66bb6a; } .err { color:#ef5350; }
            .footer { margin-top:24px; color:#607d8b; font-size:11px; }
        """);
        sb.AppendLine("</style></head><body>");
        sb.AppendLine("<h1>Multi-Agent Execution Report</h1>");

        sb.AppendLine("<div class='cards'>");
        sb.AppendLine($"<div class='card'><div class='num'>{agentRecords.Count}</div><div class='lbl'>Agents</div></div>");
        sb.AppendLine($"<div class='card'><div class='num'>{totalAll}</div><div class='lbl'>Total</div></div>");
        sb.AppendLine($"<div class='card'><div class='num ok'>{successAll}</div><div class='lbl'>Succeeded</div></div>");
        sb.AppendLine($"<div class='card fail'><div class='num'>{failedAll}</div><div class='lbl'>Failed</div></div>");
        sb.AppendLine("</div>");

        foreach (var (agentName, records) in agentRecords.OrderBy(kv => kv.Key))
        {
            var agSuccess = records.Count(r => r.Outcome == ExecutionOutcome.OutcomeSuccess);
            sb.AppendLine($"<h2>{HtmlEsc(agentName)} — {records.Count} executions ({agSuccess} ok)</h2>");
            sb.AppendLine("<table><thead><tr><th>Time</th><th>Command</th><th>Args</th><th>Exit</th><th>Duration</th><th>Outcome</th></tr></thead><tbody>");

            foreach (var r in records)
            {
                var started = r.Started?.ToDateTime().ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—";
                var duration = (r.Finished is not null && r.Started is not null)
                    ? (r.Finished.ToDateTime() - r.Started.ToDateTime()).ToString(@"hh\:mm\:ss") : "—";
                var outcome = r.Outcome switch
                {
                    ExecutionOutcome.OutcomeSuccess    => "<span class='ok'>✓ Success</span>",
                    ExecutionOutcome.OutcomeFailed     => "<span class='err'>✗ Failed</span>",
                    ExecutionOutcome.OutcomeTerminated => "<span class='err'>⊘ Terminated</span>",
                    ExecutionOutcome.OutcomeTimedOut   => "<span class='err'>⏱ Timeout</span>",
                    _                                  => "?",
                };
                var exitClass = r.ExitCode == 0 ? "ok" : "err";
                sb.AppendLine($"<tr><td>{HtmlEsc(started)}</td><td>{HtmlEsc(r.Command)}</td><td>{HtmlEsc(r.Arguments)}</td><td class='{exitClass}'>{r.ExitCode}</td><td>{HtmlEsc(duration)}</td><td>{outcome}</td></tr>");
            }
            sb.AppendLine("</tbody></table>");
        }

        sb.AppendLine($"<div class='footer'>Generated {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC</div>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static string FormatOutcome(ExecutionOutcome o) => o switch
    {
        ExecutionOutcome.OutcomeSuccess    => "Success",
        ExecutionOutcome.OutcomeFailed     => "Failed",
        ExecutionOutcome.OutcomeTerminated => "Terminated",
        ExecutionOutcome.OutcomeTimedOut   => "TimedOut",
        _                                  => "Unknown",
    };

    private static string Esc(string s) => s.Replace("\"", "\"\"");
    private static string HtmlEsc(string s) => System.Net.WebUtility.HtmlEncode(s);

    private void OnEventReceived(string address, ExecutionEvent evt)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var agent = Agents.FirstOrDefault(a => a.Address == address);
            agent?.HandleEvent(evt);
        });
    }

    private void OnConnectionChanged(string address, bool connected)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var agent = Agents.FirstOrDefault(a => a.Address == address);
            agent?.SetConnected(connected);
            StatusMessage = connected
                ? $"Connected to {address}"
                : $"Disconnected from {address} — retrying…";
        });
    }

    public void Dispose()
    {
        _connectionManager.EventReceived -= OnEventReceived;
        _connectionManager.ConnectionStateChanged -= OnConnectionChanged;
        _timeline.Dispose();
    }
}
