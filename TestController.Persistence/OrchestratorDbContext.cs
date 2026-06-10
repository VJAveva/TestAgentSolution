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
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            """;
        cmd.ExecuteNonQuery();
    }
}
