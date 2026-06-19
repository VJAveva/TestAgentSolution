using System.IO;
using System.Net.Mail;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TestController.Api.Services;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Services;

/// <summary>
/// Hosted service that listens for ExecutionCompletedEvent and dispatches automatic
/// alert emails when detector thresholds are crossed. Controller host only.
/// Reuses existing detectors (ConsecutiveFailureDetector, FlakyTestDetector),
/// existing HTML generator (BuildReportHtmlGenerator.GenerateAlertEmailHtml),
/// and existing SMTP config (BuildResultsConfig).
/// Per Phase 8 — does NOT duplicate CI batch email; fires only on threshold crossings.
/// </summary>
public sealed class NotificationDispatcher : BackgroundService
{
    private readonly IEventAggregator _events;
    private readonly ConsecutiveFailureDetector _failureDetector;
    private readonly FlakyTestDetector _flakyDetector;
    private readonly TrxResultsParser _parser;
    private readonly BuildReportHtmlGenerator _htmlGenerator;
    private readonly BuildResultsConfig _resultsConfig;
    private readonly MuteService _muteService;
    private readonly IOptionsMonitor<NotificationOptions> _options;
    private readonly IAppLogger _logger;
    private IDisposable? _subscription;

    public NotificationDispatcher(
        IEventAggregator events,
        ConsecutiveFailureDetector failureDetector,
        FlakyTestDetector flakyDetector,
        TrxResultsParser parser,
        BuildReportHtmlGenerator htmlGenerator,
        BuildResultsConfig resultsConfig,
        MuteService muteService,
        IOptionsMonitor<NotificationOptions> options,
        IAppLogger logger)
    {
        _events = events;
        _failureDetector = failureDetector;
        _flakyDetector = flakyDetector;
        _parser = parser;
        _htmlGenerator = htmlGenerator;
        _resultsConfig = resultsConfig;
        _muteService = muteService;
        _options = options;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscription = _events.Subscribe<ExecutionCompletedEvent>(evt =>
        {
            // Fire-and-forget — dispatcher does not block the event pipeline
            _ = Task.Run(() => OnExecutionCompleted(evt, stoppingToken), stoppingToken);
        });
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        base.Dispose();
    }

    private async Task OnExecutionCompleted(ExecutionCompletedEvent evt, CancellationToken ct)
    {
        try
        {
            var opts = _options.CurrentValue;
            if (!opts.AutoAlertEnabled) return;
            if (string.IsNullOrWhiteSpace(_resultsConfig.QaAlertRecipients)) return;

            // Only process runs with failures (optimization)
            if (evt.Failed <= 0 && opts.AlertOnThresholdOnly) return;

            // Run detectors against the results path
            var resultsPath = _resultsConfig.ResultsRootPath;
            if (string.IsNullOrWhiteSpace(resultsPath) || !Directory.Exists(resultsPath)) return;

            var threshold = _resultsConfig.ConsecutiveFailThreshold;
            var alerts = _failureDetector.Detect(resultsPath, _parser, threshold);

            if (opts.AlertOnThresholdOnly && alerts.Count == 0)
                return; // No threshold crossings — skip

            // Filter: remove muted targets and those in cooldown
            var eligible = new List<ConsecutiveFailureAlert>();
            foreach (var alert in alerts)
            {
                var target = alert.TestName ?? evt.WatchItemTag;
                if (await _muteService.IsMutedAsync(target, "Pipeline", ct)) continue;
                if (await _muteService.IsMutedAsync(target, "Test", ct)) continue;
                if (await _muteService.IsInCooldownAsync(target, "Test", opts.CooldownHours, ct)) continue;
                eligible.Add(alert);
            }

            if (eligible.Count == 0) return;

            // Generate and send the alert email
            var html = _htmlGenerator.GenerateAlertEmailHtml(eligible);
            var subject = $"[QA Alert] {eligible.Count} test(s) crossing failure threshold — {evt.WatchItemTag}";

            SendAlertEmail(subject, html);

            // Record cooldowns for sent targets
            foreach (var alert in eligible)
            {
                var target = alert.TestName ?? evt.WatchItemTag;
                await _muteService.RecordSentAsync(target, "Test", ct);
            }

            _logger.Info("NotificationDispatcher",
                $"Sent alert for {eligible.Count} test(s) in pipeline '{evt.WatchItemTag}'");
        }
        catch (Exception ex)
        {
            _logger.Error("NotificationDispatcher", "Failed to dispatch notification", ex);
        }
    }

    private void SendAlertEmail(string subject, string htmlBody)
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
            _logger.Error("NotificationDispatcher", "SMTP send failed", ex);
        }
    }
}
