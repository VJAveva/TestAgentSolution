using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestAgentGrpc.Services;

/// <summary>
/// Generates execution reports in CSV, JSON, and HTML formats from
/// <see cref="ExecutionTracker"/> history and <see cref="AuditLogger"/> data.
/// </summary>
public static class ReportGenerator
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ?? CSV ????????????????????????????????????????????????????????????

    public static string GenerateCsv(IReadOnlyCollection<ExecutionRecord> records)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Time,Command,Arguments,ExitCode,Duration,Outcome,StdoutLineCount,StderrLineCount");

        foreach (var r in records)
        {
            var started = r.Started?.ToDateTime().ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "";
            var duration = (r.Finished is not null && r.Started is not null)
                ? (r.Finished.ToDateTime() - r.Started.ToDateTime()).ToString(@"hh\:mm\:ss")
                : "";
            var outcome = r.Outcome switch
            {
                ExecutionOutcome.OutcomeSuccess    => "Success",
                ExecutionOutcome.OutcomeFailed     => "Failed",
                ExecutionOutcome.OutcomeTerminated => "Terminated",
                ExecutionOutcome.OutcomeTimedOut   => "TimedOut",
                _                                  => "Unknown",
            };
            var cmd = Escape(r.Command);
            var args = Escape(r.Arguments);

            sb.AppendLine($"\"{started}\",\"{cmd}\",\"{args}\",{r.ExitCode},\"{duration}\",\"{outcome}\",{r.StdoutLines.Count},{r.StderrLines.Count}");
        }

        return sb.ToString();
    }

    // ?? JSON ???????????????????????????????????????????????????????????

    public static string GenerateJson(
        string agentName, IReadOnlyCollection<ExecutionRecord> records)
    {
        var successCount = records.Count(r => r.Outcome == ExecutionOutcome.OutcomeSuccess);
        var failedCount = records.Count(r => r.Outcome != ExecutionOutcome.OutcomeSuccess);

        var report = new
        {
            GeneratedUtc = DateTime.UtcNow.ToString("O"),
            AgentName = agentName,
            Summary = new
            {
                TotalExecutions = records.Count,
                Succeeded = successCount,
                Failed = failedCount,
            },
            Executions = records.Select(r => new
            {
                r.ExecutionId,
                r.Command,
                r.Arguments,
                Started = r.Started?.ToDateTime().ToString("O"),
                Finished = r.Finished?.ToDateTime().ToString("O"),
                r.ExitCode,
                Outcome = r.Outcome.ToString(),
                ErrorMessage = string.IsNullOrEmpty(r.ErrorMessage) ? null : r.ErrorMessage,
                StdoutLineCount = r.StdoutLines.Count,
                StderrLineCount = r.StderrLines.Count,
            }).ToArray(),
        };

        return JsonSerializer.Serialize(report, s_jsonOptions);
    }

    // ?? HTML ???????????????????????????????????????????????????????????

    public static string GenerateHtml(
        string agentName, IReadOnlyCollection<ExecutionRecord> records)
    {
        var successCount = records.Count(r => r.Outcome == ExecutionOutcome.OutcomeSuccess);
        var failedCount = records.Count(r => r.Outcome != ExecutionOutcome.OutcomeSuccess);

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'/>");
        sb.AppendLine($"<title>Execution Report — {Esc(agentName)}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("""
            body { background:#1a1a2e; color:#e0e0e0; font-family:'Segoe UI',sans-serif; margin:20px; }
            h1 { color:#00c9a7; }
            .cards { display:flex; gap:16px; margin:16px 0; }
            .card { background:#16213e; border-radius:8px; padding:16px 24px; min-width:120px; }
            .card .num { font-size:28px; font-weight:bold; color:#00c9a7; }
            .card .lbl { font-size:12px; color:#90a4ae; }
            .fail .num { color:#ef5350; }
            table { border-collapse:collapse; width:100%; margin-top:16px; }
            th { background:#0f3460; color:#90a4ae; text-align:left; padding:8px 12px; font-size:12px; }
            td { padding:6px 12px; border-bottom:1px solid #16213e; font-size:12px; }
            tr:hover { background:#16213e; }
            .ok { color:#66bb6a; } .err { color:#ef5350; }
            .footer { margin-top:24px; color:#607d8b; font-size:11px; }
        """);
        sb.AppendLine("</style></head><body>");
        sb.AppendLine($"<h1>Execution Report — {Esc(agentName)}</h1>");

        // Summary cards
        sb.AppendLine("<div class='cards'>");
        sb.AppendLine($"<div class='card'><div class='num'>{records.Count}</div><div class='lbl'>Total</div></div>");
        sb.AppendLine($"<div class='card'><div class='num ok'>{successCount}</div><div class='lbl'>Succeeded</div></div>");
        sb.AppendLine($"<div class='card fail'><div class='num'>{failedCount}</div><div class='lbl'>Failed</div></div>");
        sb.AppendLine("</div>");

        // Table
        sb.AppendLine("<table><thead><tr><th>Time</th><th>Command</th><th>Args</th><th>Exit</th><th>Duration</th><th>Outcome</th><th>Stdout</th><th>Stderr</th></tr></thead><tbody>");
        foreach (var r in records)
        {
            var started = r.Started?.ToDateTime().ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—";
            var duration = (r.Finished is not null && r.Started is not null)
                ? (r.Finished.ToDateTime() - r.Started.ToDateTime()).ToString(@"hh\:mm\:ss")
                : "—";
            var outcome = r.Outcome switch
            {
                ExecutionOutcome.OutcomeSuccess    => "<span class='ok'>? Success</span>",
                ExecutionOutcome.OutcomeFailed     => "<span class='err'>? Failed</span>",
                ExecutionOutcome.OutcomeTerminated => "<span class='err'>? Terminated</span>",
                ExecutionOutcome.OutcomeTimedOut   => "<span class='err'>? Timeout</span>",
                _                                  => "?",
            };
            var exitClass = r.ExitCode == 0 ? "ok" : "err";

            sb.AppendLine($"<tr><td>{Esc(started)}</td><td>{Esc(r.Command)}</td><td>{Esc(r.Arguments)}</td>" +
                $"<td class='{exitClass}'>{r.ExitCode}</td><td>{Esc(duration)}</td><td>{outcome}</td>" +
                $"<td>{r.StdoutLines.Count}</td><td>{r.StderrLines.Count}</td></tr>");
        }
        sb.AppendLine("</tbody></table>");

        sb.AppendLine($"<div class='footer'>Generated {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC</div>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    // ?? Multi-agent HTML (for Dashboard) ???????????????????????????????

    public static string GenerateMultiAgentHtml(
        Dictionary<string, IReadOnlyCollection<ExecutionRecord>> agentRecords)
    {
        var sb = new StringBuilder();
        var totalAll = agentRecords.Values.Sum(r => r.Count);
        var successAll = agentRecords.Values.Sum(r => r.Count(e => e.Outcome == ExecutionOutcome.OutcomeSuccess));
        var failedAll = totalAll - successAll;

        sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'/>");
        sb.AppendLine("<title>Multi-Agent Execution Report</title>");
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

        // Combined summary
        sb.AppendLine("<div class='cards'>");
        sb.AppendLine($"<div class='card'><div class='num'>{agentRecords.Count}</div><div class='lbl'>Agents</div></div>");
        sb.AppendLine($"<div class='card'><div class='num'>{totalAll}</div><div class='lbl'>Total</div></div>");
        sb.AppendLine($"<div class='card'><div class='num ok'>{successAll}</div><div class='lbl'>Succeeded</div></div>");
        sb.AppendLine($"<div class='card fail'><div class='num'>{failedAll}</div><div class='lbl'>Failed</div></div>");
        sb.AppendLine("</div>");

        // Per-agent sections
        foreach (var (agentName, records) in agentRecords.OrderBy(kv => kv.Key))
        {
            var agSuccess = records.Count(r => r.Outcome == ExecutionOutcome.OutcomeSuccess);
            sb.AppendLine($"<h2>{Esc(agentName)} — {records.Count} executions ({agSuccess} ok)</h2>");
            sb.AppendLine("<table><thead><tr><th>Time</th><th>Command</th><th>Args</th><th>Exit</th><th>Duration</th><th>Outcome</th></tr></thead><tbody>");

            foreach (var r in records)
            {
                var started = r.Started?.ToDateTime().ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—";
                var duration = (r.Finished is not null && r.Started is not null)
                    ? (r.Finished.ToDateTime() - r.Started.ToDateTime()).ToString(@"hh\:mm\:ss")
                    : "—";
                var outcome = r.Outcome switch
                {
                    ExecutionOutcome.OutcomeSuccess    => "<span class='ok'>? Success</span>",
                    ExecutionOutcome.OutcomeFailed     => "<span class='err'>? Failed</span>",
                    ExecutionOutcome.OutcomeTerminated => "<span class='err'>? Terminated</span>",
                    ExecutionOutcome.OutcomeTimedOut   => "<span class='err'>? Timeout</span>",
                    _                                  => "?",
                };
                var exitClass = r.ExitCode == 0 ? "ok" : "err";

                sb.AppendLine($"<tr><td>{Esc(started)}</td><td>{Esc(r.Command)}</td><td>{Esc(r.Arguments)}</td>" +
                    $"<td class='{exitClass}'>{r.ExitCode}</td><td>{Esc(duration)}</td><td>{outcome}</td></tr>");
            }

            sb.AppendLine("</tbody></table>");
        }

        sb.AppendLine($"<div class='footer'>Generated {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC</div>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    public static string GenerateMultiAgentCsv(
        Dictionary<string, IReadOnlyCollection<ExecutionRecord>> agentRecords)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Agent,Time,Command,Arguments,ExitCode,Duration,Outcome,StdoutLineCount,StderrLineCount");

        foreach (var (agentName, records) in agentRecords.OrderBy(kv => kv.Key))
        {
            foreach (var r in records)
            {
                var started = r.Started?.ToDateTime().ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "";
                var duration = (r.Finished is not null && r.Started is not null)
                    ? (r.Finished.ToDateTime() - r.Started.ToDateTime()).ToString(@"hh\:mm\:ss")
                    : "";
                var outcome = r.Outcome switch
                {
                    ExecutionOutcome.OutcomeSuccess    => "Success",
                    ExecutionOutcome.OutcomeFailed     => "Failed",
                    ExecutionOutcome.OutcomeTerminated => "Terminated",
                    ExecutionOutcome.OutcomeTimedOut   => "TimedOut",
                    _                                  => "Unknown",
                };

                sb.AppendLine($"\"{Escape(agentName)}\",\"{started}\",\"{Escape(r.Command)}\",\"{Escape(r.Arguments)}\",{r.ExitCode},\"{duration}\",\"{outcome}\",{r.StdoutLines.Count},{r.StderrLines.Count}");
            }
        }

        return sb.ToString();
    }

    public static string GenerateMultiAgentJson(
        Dictionary<string, IReadOnlyCollection<ExecutionRecord>> agentRecords)
    {
        var totalAll = agentRecords.Values.Sum(r => r.Count);
        var successAll = agentRecords.Values.Sum(r => r.Count(e => e.Outcome == ExecutionOutcome.OutcomeSuccess));

        var report = new
        {
            GeneratedUtc = DateTime.UtcNow.ToString("O"),
            Summary = new
            {
                AgentCount = agentRecords.Count,
                TotalExecutions = totalAll,
                Succeeded = successAll,
                Failed = totalAll - successAll,
            },
            Agents = agentRecords.OrderBy(kv => kv.Key).Select(kv => new
            {
                AgentName = kv.Key,
                Executions = kv.Value.Select(r => new
                {
                    r.ExecutionId,
                    r.Command,
                    r.Arguments,
                    Started = r.Started?.ToDateTime().ToString("O"),
                    Finished = r.Finished?.ToDateTime().ToString("O"),
                    r.ExitCode,
                    Outcome = r.Outcome.ToString(),
                    StdoutLineCount = r.StdoutLines.Count,
                    StderrLineCount = r.StderrLines.Count,
                }).ToArray(),
            }).ToArray(),
        };

        return JsonSerializer.Serialize(report, s_jsonOptions);
    }

    // ?? Helpers ????????????????????????????????????????????????????????

    private static string Escape(string s) => s.Replace("\"", "\"\"");
    private static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s);
}
