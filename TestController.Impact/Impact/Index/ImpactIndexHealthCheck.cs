using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace TestControllerGrpc.Core.Impact.Index;

/// <summary>
/// Reads the index file directly with ADO.NET rather than through EF Core, so a corrupt or truncated
/// database surfaces as <see cref="ImpactIndexStatus.Corrupt"/> instead of an opaque EF exception.
/// Lives in the Impact project because Core carries no SQLite dependency; the contract stays in Core.
/// </summary>
public sealed class ImpactIndexHealthCheck : IImpactIndexHealthCheck
{
    private readonly IImpactIndexPathProvider _paths;
    private readonly ImpactMappingOptions _options;

    // RetrievalIndexStore gates every read on this, so an uncached check would put its cost on the query
    // path. Measured: PRAGMA integrity_check on a 1.29 GB index takes ~95s. Hence quick_check plus a short
    // cache keyed on the file's identity — a rebuild changes size/mtime and invalidates it immediately.
    private readonly Lock _gate = new();
    private ImpactIndexHealth? _cached;
    private (long Size, DateTime WriteUtc) _cachedStamp;
    private DateTimeOffset _cachedAtUtc;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    public ImpactIndexHealthCheck(IImpactIndexPathProvider paths, IOptions<ImpactMappingOptions> options)
    {
        _paths = paths;
        _options = options.Value;
    }

    public async Task<ImpactIndexHealth> CheckAsync(CancellationToken ct = default)
    {
        string path = _paths.IndexFilePath;

        if (!File.Exists(path))
        {
            Invalidate();
            return Unhealthy(ImpactIndexStatus.Missing, path, 0,
                $"No impact index at '{path}'. Impact queries cannot run. Rebuild with: {RebuildCommand}");
        }

        long size;
        DateTime writeUtc;
        try
        {
            var info = new FileInfo(path);
            size = info.Length;
            writeUtc = info.LastWriteTimeUtc;
        }
        catch (IOException ex)
        {
            Invalidate();
            return Unhealthy(ImpactIndexStatus.Corrupt, path, 0,
                $"Impact index at '{path}' could not be read ({ex.Message}). Rebuild with: {RebuildCommand}");
        }

        lock (_gate)
        {
            if (_cached is not null
                && _cachedStamp == (size, writeUtc)
                && DateTimeOffset.UtcNow - _cachedAtUtc < CacheTtl)
                return _cached;
        }

        ImpactIndexHealth health = await InspectAsync(path, size, ct).ConfigureAwait(false);

        lock (_gate)
        {
            _cached = health;
            _cachedStamp = (size, writeUtc);
            _cachedAtUtc = DateTimeOffset.UtcNow;
        }
        return health;
    }

    private void Invalidate()
    {
        lock (_gate) _cached = null;
    }

    private async Task<ImpactIndexHealth> InspectAsync(string path, long size, CancellationToken ct)
    {
        try
        {
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                // Pooling would leave a handle on the index after the check returns, which blocks
                // Rebuild-ImpactIndex.ps1 -Force from deleting the file it is about to regenerate.
                Pooling = false,
            };
            await using var cn = new SqliteConnection(csb.ToString());
            await cn.OpenAsync(ct).ConfigureAwait(false);

            // quick_check skips the per-page verification that makes integrity_check O(file size).
            var integrity = await ScalarAsync(cn, "PRAGMA quick_check(1);", ct).ConfigureAwait(false);
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                return Unhealthy(ImpactIndexStatus.Corrupt, path, size,
                    $"Impact index at '{path}' failed quick_check ({integrity ?? "no result"}). Rebuild with: {RebuildCommand}");

            var countText = await ScalarAsync(cn, "SELECT COUNT(*) FROM IndexedDocuments;", ct).ConfigureAwait(false);
            if (!int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int documents))
                return Unhealthy(ImpactIndexStatus.Corrupt, path, size,
                    $"Impact index at '{path}' has no readable IndexedDocuments table. Rebuild with: {RebuildCommand}");

            if (documents == 0)
                return Unhealthy(ImpactIndexStatus.Empty, path, size,
                    $"Impact index at '{path}' contains 0 documents. Impact queries would return nothing. Rebuild with: {RebuildCommand}");

            DateTimeOffset? builtUtc = null;
            try
            {
                // Table name is IndexMetadata (see ImpactIndexDbContext.OnModelCreating), not the DbSet name.
                // Only drives staleness, so a failure here must not condemn an otherwise healthy index.
                var builtText = await ScalarAsync(cn, "SELECT Value FROM IndexMetadata WHERE Key = 'BuiltUtc';", ct).ConfigureAwait(false);
                if (builtText is not null &&
                    DateTimeOffset.TryParse(builtText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                    builtUtc = parsed;
            }
            catch (SqliteException)
            {
                builtUtc = null;
            }

            if (builtUtc is { } b && DateTimeOffset.UtcNow - b > _options.IndexStaleAfter)
                return new ImpactIndexHealth
                {
                    Status = ImpactIndexStatus.Stale,
                    IndexFilePath = path,
                    SizeBytes = size,
                    DocumentCount = documents,
                    LastBuiltUtc = builtUtc,
                    Message = $"Impact index at '{path}' was built {b.LocalDateTime:yyyy-MM-dd HH:mm} " +
                              $"({(int)(DateTimeOffset.UtcNow - b).TotalDays} days ago) and may miss recent work items. " +
                              $"Refresh with: {RebuildCommand}",
                };

            return new ImpactIndexHealth
            {
                Status = ImpactIndexStatus.Ready,
                IndexFilePath = path,
                SizeBytes = size,
                DocumentCount = documents,
                LastBuiltUtc = builtUtc,
                Message = $"Impact index ready: {documents:N0} documents, {size / 1024d / 1024d:N1} MB, at '{path}'.",
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never fall back to Ready — an unknown failure is still a failure the operator must see.
            return Unhealthy(ImpactIndexStatus.Corrupt, path, size,
                $"Impact index at '{path}' could not be opened ({ex.GetType().Name}: {ex.Message}). Rebuild with: {RebuildCommand}");
        }
    }

    private static string RebuildCommand => @"powershell -File deploy\Rebuild-ImpactIndex.ps1";

    private static ImpactIndexHealth Unhealthy(ImpactIndexStatus status, string path, long size, string message) => new()
    {
        Status = status,
        IndexFilePath = path,
        SizeBytes = size,
        Message = message,
    };

    private static async Task<string?> ScalarAsync(SqliteConnection cn, string sql, CancellationToken ct)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        object? value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value?.ToString();
    }
}
