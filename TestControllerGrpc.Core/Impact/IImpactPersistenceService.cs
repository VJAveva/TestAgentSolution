namespace TestControllerGrpc.Core.Impact;

/// <summary>Outcome of a persistence operation. Never throws to the caller; failures are reported, not raised.</summary>
public sealed record PersistenceResult
{
    public required bool Succeeded { get; init; }
    public required string Message { get; init; }
    public string? Path { get; init; }
    public long SizeBytes { get; init; }
    public bool Skipped { get; init; }

    public static PersistenceResult Skip(string message) => new() { Succeeded = true, Skipped = true, Message = message };
    public static PersistenceResult Ok(string message, string? path = null, long size = 0) =>
        new() { Succeeded = true, Message = message, Path = path, SizeBytes = size };
    public static PersistenceResult Fail(string message) => new() { Succeeded = false, Message = message };
}

/// <summary>
/// Mirrors the durable learning store to a network location that survives controller VM snapshot reverts,
/// and restores it on startup when the local copy is gone.
/// </summary>
/// <remarks>
/// Only the learning store is protected by default. The retrieval index is regenerable by design and far
/// larger, so copying it every cycle would cost more than rebuilding it; enable
/// <c>ImpactMapping:Persistence:IncludeIndex</c> if a rebuild is more expensive than the transfer.
/// </remarks>
public interface IImpactPersistenceService
{
    /// <summary>Snapshots the local databases to the network location.</summary>
    Task<PersistenceResult> BackupAsync(CancellationToken ct = default);

    /// <summary>
    /// Restores from the network location only when the local copy is absent or empty. Existing local data
    /// is never overwritten — a live store is always assumed newer than any snapshot.
    /// </summary>
    Task<PersistenceResult> RestoreIfMissingAsync(CancellationToken ct = default);

    /// <summary>
    /// Folds every host's snapshot into a canonical copy on the share, then folds that canonical copy back
    /// into the local store. Outcome rows are append-only facts, so this is a set union — no conflict
    /// resolution, no locking, and each host keeps working when the share or another host is unavailable.
    /// </summary>
    Task<MergeResult> MergeAsync(CancellationToken ct = default);
}

/// <summary>What a merge cycle folded together.</summary>
public sealed record MergeResult
{
    public required bool Succeeded { get; init; }
    public required string Message { get; init; }
    public bool Skipped { get; init; }

    /// <summary>Host snapshots folded into the canonical copy.</summary>
    public int SourcesMerged { get; init; }

    /// <summary>Rows added to the canonical copy this cycle.</summary>
    public int RowsIntoCanonical { get; init; }

    /// <summary>Rows the local store gained from other hosts this cycle.</summary>
    public int RowsIntoLocal { get; init; }

    public static MergeResult Skip(string message) => new() { Succeeded = true, Skipped = true, Message = message };
    public static MergeResult Fail(string message) => new() { Succeeded = false, Message = message };
}

/// <summary>Network-persistence knobs, bound from <c>ImpactMapping:Persistence</c>.</summary>
public sealed class ImpactPersistenceOptions
{
    /// <summary>Off unless a network path is configured.</summary>
    public bool Enabled { get; set; }

    /// <summary>UNC folder that survives snapshot reverts, e.g. <c>\\dev\link\DevTransfer\VJPersistence</c>.</summary>
    public string NetworkPath { get; set; } = "";

    /// <summary>How often the learning store is mirrored. Snapshot reverts are unannounced, so this is the
    /// real protection window — a graceful-shutdown-only backup would miss a hard revert.</summary>
    public TimeSpan BackupInterval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Delay before the first backup, so startup work finishes first.</summary>
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Timestamped copies kept alongside the current one, for rollback.</summary>
    public int KeepVersions { get; set; } = 5;

    /// <summary>Also mirror the retrieval index. Off by default: it is regenerable and can exceed 1 GB.</summary>
    public bool IncludeIndex { get; set; }

    /// <summary>
    /// Fold every host's snapshot into a shared canonical store and pull the result back down, so each host
    /// learns from runs executed on the others. Off leaves each host's learning private to itself.
    /// </summary>
    public bool MergeAcrossHosts { get; set; } = true;

    /// <summary>Folder under <see cref="NetworkPath"/> holding the merged store. Leading underscore keeps it
    /// out of the per-machine namespace.</summary>
    public string CanonicalFolderName { get; set; } = "_canonical";

    /// <summary>
    /// Name of this host's folder on the share. Empty means <see cref="Environment.MachineName"/>, which is
    /// right for the normal one-impact-host-per-machine layout. Set it only when two impact hosts share a
    /// machine, so they do not overwrite each other's snapshot.
    /// </summary>
    public string HostId { get; set; } = "";
}
