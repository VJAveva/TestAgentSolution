using System.Net.Mail;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Services;

/// <summary>
/// Hosted service that watches fleet health and emits operational alert emails:
/// <list type="bullet">
///   <item><b>Agent-down</b> — when the dispatcher's <see cref="AgentHealthState"/> for
///   an agent transitions to unhealthy (circuit open / connection lost), with a
///   re-alert cooldown so a persistently-down agent doesn't spam every tick.</item>
///   <item><b>Run-overrun</b> — when an active session runs longer than the configured
///   threshold, alerted once per session.</item>
/// </list>
/// Controller host only. Reuses the existing SMTP config (<see cref="BuildResultsConfig"/>)
/// and recipient list — does NOT duplicate the threshold/CI emails owned by
/// <see cref="NotificationDispatcher"/>. Polls every <c>FleetCheckIntervalSeconds</c>.
/// </summary>
public sealed class FleetAlertDispatcher : BackgroundService
{
    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly ExecutionSessionManager _sessionManager;
    private readonly BuildResultsConfig _resultsConfig;
    private readonly IOptionsMonitor<NotificationOptions> _options;
    private readonly IAppLogger _logger;

    // Last-known healthy flag per agent; used to detect transitions (null = unseen).
    private readonly Dictionary<string, bool> _lastHealthy = new(StringComparer.OrdinalIgnoreCase);
    // Last time we alerted that an agent is down; used to throttle re-alerts.
    private readonly Dictionary<string, DateTime> _lastDownAlertUtc = new(StringComparer.OrdinalIgnoreCase);
    // Sessions we have already raised an overrun alert for (cleared when no longer active).
    private readonly HashSet<string> _overrunAlerted = new(StringComparer.Ordinal);

    public FleetAlertDispatcher(
        IAgentGrpcDispatcher dispatcher,
        ExecutionSessionManager sessionManager,
        BuildResultsConfig resultsConfig,
        IOptionsMonitor<NotificationOptions> options,
        IAppLogger logger)
    {
        _dispatcher = dispatcher;
        _sessionManager = sessionManager;
        _resultsConfig = resultsConfig;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = Math.Max(5, _options.CurrentValue.FleetCheckIntervalSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    Tick();
                }
                catch (Exception ex)
                {
                    _logger.Error("FleetAlertDispatcher", "Fleet alert tick failed", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private void Tick()
    {
        var opts = _options.CurrentValue;
        if (string.IsNullOrWhiteSpace(_resultsConfig.QaAlertRecipients)) return;

        if (opts.AgentDownAlertEnabled) CheckAgentHealth(opts);
        if (opts.RunOverrunAlertEnabled) CheckRunOverruns(opts);
    }

    private void CheckAgentHealth(NotificationOptions opts)
    {
        var now = DateTime.UtcNow;
        var reAlert = TimeSpan.FromMinutes(Math.Max(1, opts.AgentDownReAlertMinutes));

        foreach (var (agent, health) in _dispatcher.GetAllAgentHealth())
        {
            var wasHealthy = _lastHealthy.TryGetValue(agent, out var prev) ? prev : true;
            var isHealthy = health.IsHealthy;
            _lastHealthy[agent] = isHealthy;

            if (!isHealthy)
            {
                // Alert on the healthy→unhealthy edge, or again once the re-alert
                // cooldown has elapsed for an agent that stays down.
                var due = !_lastDownAlertUtc.TryGetValue(agent, out var last)
                          || (now - last) >= reAlert;
                if ((wasHealthy || due) && due)
                {
                    SendAgentDownAlert(agent, health);
                    _lastDownAlertUtc[agent] = now;
                }
            }
            else if (!wasHealthy)
            {
                // Recovered — clear throttle and notify.
                _lastDownAlertUtc.Remove(agent);
                SendAgentRecoveredAlert(agent, health);
            }
        }
    }

    private void CheckRunOverruns(NotificationOptions opts)
    {
        var now = DateTime.UtcNow;
        var threshold = TimeSpan.FromMinutes(Math.Max(1, opts.RunOverrunMinutes));

        var active = _sessionManager.GetActiveSessions();
        var activeIds = new HashSet<string>(active.Select(s => s.SessionId), StringComparer.Ordinal);

        foreach (var session in active)
        {
            var elapsed = now - session.StartedUtc;
            if (elapsed < threshold) continue;
            if (!_overrunAlerted.Add(session.SessionId)) continue; // already alerted

            SendOverrunAlert(session, elapsed);
        }

        // Forget sessions that have completed so a future re-use of an id can re-alert.
        _overrunAlerted.RemoveWhere(id => !activeIds.Contains(id));
    }

    private void SendAgentDownAlert(string agent, AgentHealthState health)
    {
        var lastSuccess = health.LastSuccessUtc is { } ls
            ? ls.ToLocalTime().ToString("u")
            : "never";
        var body = new StringBuilder()
            .Append("<h2 style='color:#EF4444;margin:0 0 12px'>Agent unreachable</h2>")
            .Append($"<p><b>Agent:</b> {Html(agent)}</p>")
            .Append($"<p><b>Consecutive failures:</b> {health.ConsecutiveFailures}</p>")
            .Append($"<p><b>Last successful contact:</b> {Html(lastSuccess)}</p>")
            .Append($"<p><b>Detected:</b> {DateTime.Now:u}</p>")
            .Append("<p style='color:#9099B8'>The controller's resilience layer has marked this agent unhealthy. "
                  + "Runs targeting it will fail or queue until it recovers.</p>")
            .ToString();

        SendEmail($"[QA Alert] Agent down — {agent}", body);
        _logger.Warn("FleetAlertDispatcher", $"Agent-down alert sent for '{agent}'");
    }

    private void SendAgentRecoveredAlert(string agent, AgentHealthState health)
    {
        var body = new StringBuilder()
            .Append("<h2 style='color:#22C55E;margin:0 0 12px'>Agent recovered</h2>")
            .Append($"<p><b>Agent:</b> {Html(agent)}</p>")
            .Append($"<p><b>Recovered:</b> {DateTime.Now:u}</p>")
            .ToString();

        SendEmail($"[QA Alert] Agent recovered — {agent}", body);
        _logger.Info("FleetAlertDispatcher", $"Agent-recovered alert sent for '{agent}'");
    }

    private void SendOverrunAlert(ExecutionSession session, TimeSpan elapsed)
    {
        var agents = session.LockedAgents.Length > 0
            ? string.Join(", ", session.LockedAgents)
            : "(none)";
        var body = new StringBuilder()
            .Append("<h2 style='color:#F59E0B;margin:0 0 12px'>Run overrunning</h2>")
            .Append($"<p><b>Pipeline:</b> {Html(session.WatchItemTag)}</p>")
            .Append($"<p><b>Session:</b> {Html(session.SessionId)}</p>")
            .Append($"<p><b>Elapsed:</b> {(int)elapsed.TotalMinutes} min</p>")
            .Append($"<p><b>Agents:</b> {Html(agents)}</p>")
            .Append($"<p><b>Started:</b> {session.StartedUtc.ToLocalTime():u}</p>")
            .Append($"<p><b>Triggered by:</b> {Html(string.IsNullOrEmpty(session.UserDisplayName) ? session.UserId : session.UserDisplayName)}</p>")
            .ToString();

        SendEmail($"[QA Alert] Run overrun ({(int)elapsed.TotalMinutes} min) — {session.WatchItemTag}", body);
        _logger.Warn("FleetAlertDispatcher",
            $"Run-overrun alert sent for session '{session.SessionId}' ({(int)elapsed.TotalMinutes} min)");
    }

    private void SendEmail(string subject, string htmlBody)
    {
        try
        {
            using var smtp = new SmtpClient(_resultsConfig.SmtpServer, _resultsConfig.SmtpPort)
            {
                UseDefaultCredentials = true
            };
            using var message = new MailMessage(
                _resultsConfig.FromAddress,
                _resultsConfig.QaAlertRecipients)
            {
                Subject = subject,
                Body = htmlBody,
                IsBodyHtml = true,
            };
            smtp.Send(message);
        }
        catch (Exception ex)
        {
            _logger.Error("FleetAlertDispatcher", "SMTP send failed", ex);
        }
    }

    private static string Html(string value)
        => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
}
