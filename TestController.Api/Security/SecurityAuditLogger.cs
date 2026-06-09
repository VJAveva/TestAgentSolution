using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TestController.Api.Security;

/// <summary>
/// Security-specific audit logger writing to a separate sink from application logs.
/// Records authentication, authorization, and admin action events.
/// </summary>
public interface ISecurityAuditLogger
{
    void LogAuthentication(string identity, string mode, string outcome, string? sourceIp = null);
    void LogAuthorization(string identity, string policy, string resource, string decision);
    void LogAdminAction(string identity, string action, string target, string? detail = null);
    void LogCommandRejection(string identity, string agent, string command, string reason);
}

public sealed class SecurityAuditLogger : ISecurityAuditLogger, IDisposable
{
    private readonly SecurityAuditOptions _options;
    private readonly ILogger<SecurityAuditLogger> _logger;
    private readonly BlockingCollection<SecurityAuditEntry> _queue = new(10_000);
    private readonly Thread? _writerThread;
    private readonly string _logDirectory;
    private readonly Timer? _cleanupTimer;

    public SecurityAuditLogger(IOptions<SecurityOptions> options, ILogger<SecurityAuditLogger> logger)
    {
        _options = options.Value.Audit;
        _logger = logger;
        _logDirectory = _options.LogPath;

        if (_options.Enabled)
        {
            try
            {
                Directory.CreateDirectory(_logDirectory);
                _writerThread = new Thread(ProcessQueue) { IsBackground = true, Name = "SecurityAuditWriter" };
                _writerThread.Start();

                // Run cleanup once per day to enforce RetentionDays
                _cleanupTimer = new Timer(_ => CleanupOldLogs(), null,
                    TimeSpan.FromMinutes(5), TimeSpan.FromHours(24));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cannot create audit log directory '{Path}'; audit logging disabled", _logDirectory);
                _options.Enabled = false;
            }
        }
    }

    public void LogAuthentication(string identity, string mode, string outcome, string? sourceIp = null)
    {
        Enqueue(new SecurityAuditEntry
        {
            EventType = "Authentication",
            Identity = identity,
            Detail = new { mode, outcome, sourceIp }
        });
    }

    public void LogAuthorization(string identity, string policy, string resource, string decision)
    {
        Enqueue(new SecurityAuditEntry
        {
            EventType = "Authorization",
            Identity = identity,
            Detail = new { policy, resource, decision }
        });
    }

    public void LogAdminAction(string identity, string action, string target, string? detail = null)
    {
        Enqueue(new SecurityAuditEntry
        {
            EventType = "AdminAction",
            Identity = identity,
            Detail = new { action, target, detail }
        });
    }

    public void LogCommandRejection(string identity, string agent, string command, string reason)
    {
        Enqueue(new SecurityAuditEntry
        {
            EventType = "CommandRejection",
            Identity = identity,
            Detail = new { agent, command, reason }
        });
    }

    private void Enqueue(SecurityAuditEntry entry)
    {
        if (!_options.Enabled) return;

        if (!_queue.TryAdd(entry))
        {
            _logger.LogWarning("Security audit queue full — dropping entry: {Event} for {Identity}",
                entry.EventType, entry.Identity);
        }
    }

    private void ProcessQueue()
    {
        foreach (var entry in _queue.GetConsumingEnumerable())
        {
            try
            {
                var fileName = $"security-audit-{DateTime.UtcNow:yyyy-MM-dd}.jsonl";
                var filePath = Path.Combine(_logDirectory, fileName);
                var json = JsonSerializer.Serialize(entry);
                File.AppendAllText(filePath, json + Environment.NewLine);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write security audit entry");
            }
        }
    }

    public void CleanupOldLogs()
    {
        if (!_options.Enabled || _options.RetentionDays <= 0) return;

        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-_options.RetentionDays);
            var files = Directory.GetFiles(_logDirectory, "security-audit-*.jsonl");
            foreach (var file in files)
            {
                if (File.GetCreationTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up old security audit logs");
        }
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
        _queue.CompleteAdding();
        _writerThread?.Join(TimeSpan.FromSeconds(5));
        _queue.Dispose();
    }
}

internal sealed class SecurityAuditEntry
{
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public string EventType { get; init; } = "";
    public string Identity { get; init; } = "";
    public object? Detail { get; init; }
}
