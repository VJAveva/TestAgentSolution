using Microsoft.EntityFrameworkCore;

namespace TestControllerGrpc.Core.Impact.Index;

/// <summary>
/// Creates the index schema and applies SQLite pragmas (P06). The index holds no durable business data
/// (it is rebuilt from Azure DevOps at any time), so it is provisioned with EnsureCreated rather than
/// migrations, and runs in WAL mode so readers can query while a rebuild writes.
/// </summary>
public static class ImpactIndexInitializer
{
    /// <summary>Metadata key holding the schema version this index was built with.</summary>
    public const string SchemaVersionKey = "SchemaVersion";

    /// <summary>
    /// Bump whenever the index schema changes shape. <see cref="EnsureCreatedAsync"/> drops and recreates a
    /// database stamped with an older version, because EnsureCreated only creates missing tables — it never
    /// ALTERs, so a schema change would otherwise leave the existing index unreadable.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Ensures the schema exists, is current, and enables WAL journalling. Safe to call repeatedly.</summary>
    public static async Task EnsureCreatedAsync(
        ImpactIndexDbContext context, bool rebuildOnSchemaChange = true, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        await context.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);

        int? stored = await ReadSchemaVersionAsync(context, ct).ConfigureAwait(false);
        if (stored is not null && stored != CurrentSchemaVersion)
        {
            if (!rebuildOnSchemaChange)
            {
                throw new InvalidOperationException(
                    $"Impact index schema version {stored} does not match {CurrentSchemaVersion} and " +
                    "ImpactMapping:Index:RebuildOnSchemaChange is disabled. Delete the index or enable the option.");
            }

            await context.Database.EnsureDeletedAsync(ct).ConfigureAwait(false);
            await context.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
        }

        // A null stored version means either a fresh database or an index built before versioning existed.
        // Both are stamped rather than dropped: the pre-versioning shape IS version 1, so dropping would
        // force a multi-hour rebuild of a perfectly readable production index.
        if (stored != CurrentSchemaVersion)
        {
            await WriteSchemaVersionAsync(context, ct).ConfigureAwait(false);
        }

        // WAL persists in the database header; NORMAL sync is safe for a rebuildable cache and far faster.
        await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct).ConfigureAwait(false);
        await context.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;", ct).ConfigureAwait(false);
    }

    private static async Task<int?> ReadSchemaVersionAsync(ImpactIndexDbContext context, CancellationToken ct)
    {
        IndexMetadata? row = await context.Metadata
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Key == SchemaVersionKey, ct)
            .ConfigureAwait(false);

        return row is not null && int.TryParse(row.Value, out int version) ? version : null;
    }

    private static async Task WriteSchemaVersionAsync(ImpactIndexDbContext context, CancellationToken ct)
    {
        IndexMetadata? row = await context.Metadata
            .FirstOrDefaultAsync(m => m.Key == SchemaVersionKey, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            context.Metadata.Add(new IndexMetadata { Key = SchemaVersionKey, Value = CurrentSchemaVersion.ToString() });
        }
        else
        {
            row.Value = CurrentSchemaVersion.ToString();
        }

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
