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

    /// <summary>The shape indexes had before versioning was introduced.</summary>
    private const int OriginalSchemaVersion = 1;

    /// <summary>
    /// Bump whenever the index schema changes shape. <see cref="EnsureCreatedAsync"/> drops and recreates a
    /// database stamped with an older version, because EnsureCreated only creates missing tables — it never
    /// ALTERs, so a schema change would otherwise leave the existing index unreadable.
    /// <para>
    /// 2: test case documents now include System.Description. Incremental builds only revisit items ADO
    /// reports as changed, so without a forced rebuild existing documents would never gain the new text.
    /// </para>
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>Ensures the schema exists, is current, and enables WAL journalling. Safe to call repeatedly.</summary>
    /// <param name="rebuildOnSchemaChange">
    /// Writers pass true and drop-and-recreate a stale index. Readers pass false and keep using it: an index
    /// from an older version is out of date, not unreadable, and taking the reader down for the length of a
    /// rebuild is worse than serving the previous quality. A genuinely incompatible schema still surfaces —
    /// EF throws on the first query against a missing column.
    /// </param>
    public static async Task EnsureCreatedAsync(
        ImpactIndexDbContext context, bool rebuildOnSchemaChange = true, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        await context.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);

        int? stored = await ReadSchemaVersionAsync(context, ct).ConfigureAwait(false);
        bool stale = stored != CurrentSchemaVersion
            && (stored is not null || await HasDocumentsAsync(context, ct).ConfigureAwait(false));

        if (stale && rebuildOnSchemaChange)
        {
            await context.Database.EnsureDeletedAsync(ct).ConfigureAwait(false);
            await context.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
            await WriteSchemaVersionAsync(context, ct).ConfigureAwait(false);
        }
        else if (!stale && stored != CurrentSchemaVersion)
        {
            // Empty database: stamp it so the next reader knows what shape it is.
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

    private static Task<bool> HasDocumentsAsync(ImpactIndexDbContext context, CancellationToken ct)
        => context.Documents.AnyAsync(ct);

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
