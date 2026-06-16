using TestControllerGrpc.Authorization;
using TestControllerGrpc.Identity;
using TestControllerGrpc.Services;

namespace TestController.Api.Services;

/// <summary>
/// Authorized enable/disable pipeline operations for REST paths.
/// Calls PipelineAuthorizationGuard with Pipeline_Enable or Pipeline_Disable → audit.
/// Singleton DI per CONVENTIONS.md.
/// Per 02_Implementation_Roadmap.md §Phase 6.
/// </summary>
public sealed class EnableDisableService
{
    private readonly PipelineAuthorizationGuard _guard;
    private readonly IVocabularyMonitor _vocabMonitor;
    private readonly IAuditWriter _auditWriter;
    private readonly IAppLogger _logger;

    public EnableDisableService(
        PipelineAuthorizationGuard guard,
        IVocabularyMonitor vocabMonitor,
        IAppLogger logger,
        IAuditWriter auditWriter)
    {
        _guard = guard;
        _vocabMonitor = vocabMonitor;
        _logger = logger;
        _auditWriter = auditWriter;
    }

    /// <summary>
    /// Authorize and apply enable/disable state to a pipeline (WatchItem).
    /// Throws PipelineAuthorizationDeniedException on deny.
    /// </summary>
    public async Task SetEnabledAsync(IUserContext user, string pipelineId, bool enabled, CancellationToken ct = default)
    {
        var permission = enabled ? Permission.Pipeline_Enable : Permission.Pipeline_Disable;
        await _guard.AuthorizeAsync(user, permission, pipelineId, ct);

        var config = _vocabMonitor.CurrentConfig;
        var watchItem = config?.WatchItems.FirstOrDefault(wi =>
            string.Equals(wi.Tag, pipelineId, StringComparison.OrdinalIgnoreCase));

        if (watchItem is null)
            throw new InvalidOperationException($"Pipeline '{pipelineId}' not found in WatchList.");

        watchItem.IsEnabled = enabled;
        _logger.Info("EnableDisableService", $"Pipeline '{pipelineId}' set IsEnabled={enabled} by user={user.DisplayName}");

        _auditWriter.Enqueue(new AuditEntry
        {
            UserId = user.UserId,
            ActionName = enabled ? "Pipeline_Enable" : "Pipeline_Disable",
            ResourceId = pipelineId,
            Allowed = true,
            ReasonCode = enabled ? "enabled" : "disabled",
            TimestampUtc = DateTime.UtcNow,
            ClientKind = user.ClientKind,
        });
    }
}
