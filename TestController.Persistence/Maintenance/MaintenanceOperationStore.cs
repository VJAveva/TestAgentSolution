using Microsoft.EntityFrameworkCore;
using TestControllerGrpc.Core.Maintenance;

namespace TestController.Persistence.Maintenance;

/// <summary>
/// EF Core / SQLite implementation of <see cref="IMaintenanceOperationStore"/>. Singleton-safe: it opens a
/// short-lived context per call via <see cref="IDbContextFactory{TContext}"/> (the codebase pattern for accessing
/// the Scoped <see cref="OrchestratorDbContext"/> from Singleton services). <see cref="SaveAsync"/> upserts, since
/// the engine persists the same operation id repeatedly as it advances through phases.
/// </summary>
public sealed class MaintenanceOperationStore : IMaintenanceOperationStore
{
    private readonly IDbContextFactory<OrchestratorDbContext> _factory;

    public MaintenanceOperationStore(IDbContextFactory<OrchestratorDbContext> factory) => _factory = factory;

    public async Task SaveAsync(MaintenanceOperation operation, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        var id = operation.Id.ToString();
        var existing = await db.MaintenanceOperations.FindAsync([id], cancellationToken);
        if (existing is null)
            db.MaintenanceOperations.Add(ToEntity(operation));
        else
            Apply(operation, existing);

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<MaintenanceOperation?> GetAsync(Guid operationId, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var entity = await db.MaintenanceOperations.FindAsync([operationId.ToString()], cancellationToken);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task<IReadOnlyList<MaintenanceOperation>> GetHistoryAsync(
        string? nodeId, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        IQueryable<MaintenanceOperationRecord> query = db.MaintenanceOperations.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(nodeId))
            query = query.Where(m => m.NodeId == nodeId);

        // SQLite translates neither comparison nor ORDER BY over DateTimeOffset, so the date window and the
        // sort are both applied after materialisation.
        IEnumerable<MaintenanceOperationRecord> rows = await query.ToListAsync(cancellationToken);
        if (fromUtc is { } from)
            rows = rows.Where(m => m.StartedUtc >= from);
        if (toUtc is { } to)
            rows = rows.Where(m => m.StartedUtc <= to);

        return rows.OrderByDescending(m => m.StartedUtc).Select(ToDomain).ToList();
    }

    public async Task<IReadOnlyList<MaintenanceOperation>> GetUnfinishedAsync(CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        var rows = await db.MaintenanceOperations
            .AsNoTracking()
            .Where(m => m.State == MaintenanceOperationState.Queued || m.State == MaintenanceOperationState.Running)
            .ToListAsync(cancellationToken);

        return rows.OrderBy(m => m.StartedUtc).Select(ToDomain).ToList();
    }

    private static MaintenanceOperationRecord ToEntity(MaintenanceOperation op) => new()
    {
        Id = op.Id.ToString(),
        NodeId = op.NodeId,
        Kind = op.Kind,
        SnapshotName = op.SnapshotName,
        ScriptPath = op.ScriptPath,
        State = op.State,
        Phase = op.Phase,
        TriggerSource = op.TriggerSource,
        TriggeredBy = op.TriggeredBy,
        Reason = op.Reason,
        LinkedRunId = op.LinkedRunId?.ToString(),
        StartedUtc = op.StartedUtc,
        CompletedUtc = op.CompletedUtc,
        ExitCode = op.ExitCode,
        FailurePhase = op.FailurePhase,
        LogPath = op.LogPath,
    };

    private static void Apply(MaintenanceOperation op, MaintenanceOperationRecord e)
    {
        e.NodeId = op.NodeId;
        e.Kind = op.Kind;
        e.SnapshotName = op.SnapshotName;
        e.ScriptPath = op.ScriptPath;
        e.State = op.State;
        e.Phase = op.Phase;
        e.TriggerSource = op.TriggerSource;
        e.TriggeredBy = op.TriggeredBy;
        e.Reason = op.Reason;
        e.LinkedRunId = op.LinkedRunId?.ToString();
        e.StartedUtc = op.StartedUtc;
        e.CompletedUtc = op.CompletedUtc;
        e.ExitCode = op.ExitCode;
        e.FailurePhase = op.FailurePhase;
        e.LogPath = op.LogPath;
    }

    private static MaintenanceOperation ToDomain(MaintenanceOperationRecord e) => new()
    {
        Id = Guid.Parse(e.Id),
        NodeId = e.NodeId,
        Kind = e.Kind,
        SnapshotName = e.SnapshotName,
        ScriptPath = e.ScriptPath,
        State = e.State,
        Phase = e.Phase,
        TriggerSource = e.TriggerSource,
        TriggeredBy = e.TriggeredBy,
        Reason = e.Reason,
        LinkedRunId = string.IsNullOrEmpty(e.LinkedRunId) ? null : Guid.Parse(e.LinkedRunId),
        StartedUtc = e.StartedUtc,
        CompletedUtc = e.CompletedUtc,
        ExitCode = e.ExitCode,
        FailurePhase = e.FailurePhase,
        LogPath = e.LogPath,
    };
}
