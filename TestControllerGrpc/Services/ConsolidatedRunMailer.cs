using System.Net.Mail;
using Microsoft.Extensions.Hosting;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Sends one consolidated summary email when a FULL pipeline finishes. Modelled on
/// <see cref="NotificationDispatcher"/>: subscribes to <see cref="ExecutionCompletedEvent"/> and dispatches
/// fire-and-forget so mail never blocks or fails a run. Controller host only.
/// </summary>
public sealed class ConsolidatedRunMailer : BackgroundService
{
    private readonly IEventAggregator _events;
    private readonly ExecutionSessionManager _sessions;
    private readonly BuildResultsConfig _config;
    private readonly IAppLogger _logger;
    private IDisposable? _subscription;

    public ConsolidatedRunMailer(
        IEventAggregator events,
        ExecutionSessionManager sessions,
        BuildResultsConfig config,
        IAppLogger logger)
    {
        _events = events;
        _sessions = sessions;
        _config = config;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscription = _events.Subscribe<ExecutionCompletedEvent>(evt =>
            _ = Task.Run(() => OnExecutionCompleted(evt), stoppingToken));
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        base.Dispose();
    }

    private void OnExecutionCompleted(ExecutionCompletedEvent evt)
    {
        try
        {
            if (!_config.SendConsolidatedEmail) return;

            var recipients = Recipients();
            if (recipients.Length == 0 || string.IsNullOrWhiteSpace(_config.FromAddress)) return;

            // The event fires from many call sites, including single-action and group runs. Re-read the
            // session so the decision is made on real state rather than the event's summary counts.
            var session = _sessions.GetSession(evt.SessionId);
            if (session is null || !session.IsFullPipelineRun) return;

            var report = ConsolidatedRunReportBuilder.Build(session);
            var html = ConsolidatedRunEmailBuilder.BuildHtml(report, LinksFor(report));

            using var smtp = new SmtpClient(_config.SmtpServer, _config.SmtpPort) { UseDefaultCredentials = true };
            using var message = new MailMessage(_config.FromAddress, recipients)
            {
                Subject = ConsolidatedRunEmailBuilder.BuildSubject(report),
                Body = html,
                IsBodyHtml = true,
            };
            smtp.Send(message);

            _logger.Info("ConsolidatedEmail",
                $"Sent run summary for {report.PipelineTag} ({report.Verdict}, {report.AgentsPassed}/{report.AgentsTotal} agents) to {recipients}");
        }
        catch (Exception ex)
        {
            // A failed notification must never surface as a pipeline failure.
            _logger.Error("ConsolidatedEmail", $"Failed to send run summary for session {evt.SessionId}.", ex);
        }
    }

    private string Recipients()
    {
        var configured = string.IsNullOrWhiteSpace(_config.ConsolidatedRecipients)
            ? _config.ReportRecipients
            : _config.ConsolidatedRecipients;
        return (configured ?? "").Replace(';', ',').Trim();
    }

    private ConsolidatedRunEmailLinks LinksFor(ConsolidatedRunReport report)
    {
        var reportCard = _config.ConsolidatedReportCardUrl;
        reportCard = string.IsNullOrWhiteSpace(reportCard) || report.BuildNumber.Length == 0
            ? ""
            : reportCard.Replace("{build}", Uri.EscapeDataString(report.BuildNumber), StringComparison.OrdinalIgnoreCase);

        return new ConsolidatedRunEmailLinks(
            ControllerUrl: _config.ConsolidatedControllerUrl ?? "",
            ResultsShareUrl: _config.ResultsRootPath ?? "",
            ReportCardUrl: reportCard);
    }
}
