namespace TestControllerGrpc.Core.Impact;

/// <summary>Health of the on-disk retrieval index.</summary>
public enum ImpactIndexStatus
{
    /// <summary>Present, readable and populated.</summary>
    Ready,

    /// <summary>No file at the resolved path.</summary>
    Missing,

    /// <summary>File exists but holds no documents.</summary>
    Empty,

    /// <summary>File exists but cannot be opened or fails <c>PRAGMA integrity_check</c>.</summary>
    Corrupt,

    /// <summary>Readable and populated, but older than the configured threshold.</summary>
    Stale,
}

/// <summary>A point-in-time health reading for the impact index.</summary>
public sealed record ImpactIndexHealth
{
    public required ImpactIndexStatus Status { get; init; }
    public required string IndexFilePath { get; init; }
    public long SizeBytes { get; init; }
    public int DocumentCount { get; init; }
    public DateTimeOffset? LastBuiltUtc { get; init; }

    /// <summary>Operator-facing explanation. For any non-Ready status this names the path, the reason,
    /// and the exact command that fixes it.</summary>
    public required string Message { get; init; }

    public bool IsReady => Status == ImpactIndexStatus.Ready;

    /// <summary>True when the index cannot serve queries at all (as opposed to merely being stale).</summary>
    public bool IsUnusable => Status is ImpactIndexStatus.Missing or ImpactIndexStatus.Empty or ImpactIndexStatus.Corrupt;
}

/// <summary>Reports whether the impact index can serve queries. Never returns Ready as an error fallback.</summary>
public interface IImpactIndexHealthCheck
{
    Task<ImpactIndexHealth> CheckAsync(CancellationToken ct = default);
}

/// <summary>
/// Thrown when an impact query is attempted against an index that cannot serve it. Deliberately loud: test
/// selection is recall-biased, so an unavailable index must never degrade into "no impacted tests found",
/// which is indistinguishable from a genuine empty result.
/// </summary>
public sealed class ImpactIndexUnavailableException : InvalidOperationException
{
    public ImpactIndexUnavailableException(ImpactIndexHealth health)
        : base(health.Message) => Health = health;

    public ImpactIndexHealth Health { get; }
}
