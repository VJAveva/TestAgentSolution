using System.IO;
using System.Net.Mail;
using System.Text;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>Emails the regression churn report (HTML body + workbook attachment) via the existing SMTP config.</summary>
public interface IRegressionReportMailer
{
    void Send(string recipients, string subject, string htmlBody, byte[]? attachment, string attachmentFileName, string attachmentContentType);
}

/// <summary>
/// Reuses <see cref="BuildResultsConfig"/> (SmtpServer/SmtpPort/FromAddress, UseDefaultCredentials) — the
/// same path the build-results and alert mailers use — so the regression report needs no new SMTP config.
/// </summary>
public sealed class RegressionReportMailer : IRegressionReportMailer
{
    private readonly BuildResultsConfig _config;
    private readonly IAppLogger _logger;

    public RegressionReportMailer(BuildResultsConfig config, IAppLogger logger)
    {
        _config = config;
        _logger = logger;
    }

    public void Send(string recipients, string subject, string htmlBody, byte[]? attachment, string attachmentFileName, string attachmentContentType)
    {
        if (string.IsNullOrWhiteSpace(recipients))
            throw new InvalidOperationException("No recipients specified.");
        if (string.IsNullOrWhiteSpace(_config.FromAddress))
            throw new InvalidOperationException("BuildResults:FromAddress is not configured in appsettings.json.");

        using var smtp = new SmtpClient(_config.SmtpServer, _config.SmtpPort) { UseDefaultCredentials = true };
        using var message = new MailMessage(_config.FromAddress, recipients.Replace(';', ','))
        {
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true,
        };

        if (attachment is { Length: > 0 })
        {
            // Stream is owned by the Attachment and disposed when the MailMessage is disposed.
            var stream = new MemoryStream(attachment);
            message.Attachments.Add(new Attachment(stream, attachmentFileName, attachmentContentType));
        }

        smtp.Send(message);
        _logger.Info("Regression", $"Churn report emailed to {recipients}.");
    }
}
