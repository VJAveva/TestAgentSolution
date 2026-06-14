using Microsoft.EntityFrameworkCore;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Identity;

namespace TestController.Persistence;

/// <summary>
/// EF Core DbContext for the orchestrator SQLite database.
/// WAL mode + foreign keys enabled per 01_System_Design.md §7.1.
/// </summary>
public sealed class OrchestratorDbContext : DbContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<PipelineAssignment> PipelineAssignments => Set<PipelineAssignment>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public OrchestratorDbContext(DbContextOptions<OrchestratorDbContext> options)
        : base(options) { }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // WAL mode + synchronous=NORMAL + foreign keys per design spec
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqlite("Data Source=orchestrator.db");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OrchestratorDbContext).Assembly);
    }
}

/// <summary>
/// Extension to apply SQLite pragmas after connection open.
/// Called from DI registration.
/// </summary>
public static class OrchestratorDbContextExtensions
{
    public static void ApplyPragmas(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA foreign_keys = ON;
                PRAGMA busy_timeout = 5000;
                """;
            cmd.ExecuteNonQuery();
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 8)
        {
            // SQLite Error 8 = SQLITE_READONLY — database not yet created or opened for existence check.
            // Pragmas will be applied on the next writable connection.
        }
    }
}
