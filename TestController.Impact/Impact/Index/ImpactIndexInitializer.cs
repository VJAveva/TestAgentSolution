using Microsoft.EntityFrameworkCore;

namespace TestControllerGrpc.Core.Impact.Index;

/// <summary>
/// Creates the index schema and applies SQLite pragmas (P06). The index holds no durable business data
/// (it is rebuilt from Azure DevOps at any time), so it is provisioned with EnsureCreated rather than
/// migrations, and runs in WAL mode so readers can query while a rebuild writes.
/// </summary>
public static class ImpactIndexInitializer
{
    /// <summary>Ensures the schema exists and enables WAL journalling. Safe to call repeatedly.</summary>
    public static async Task EnsureCreatedAsync(ImpactIndexDbContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        await context.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);

        // WAL persists in the database header; NORMAL sync is safe for a rebuildable cache and far faster.
        await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct).ConfigureAwait(false);
        await context.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;", ct).ConfigureAwait(false);
    }
}
