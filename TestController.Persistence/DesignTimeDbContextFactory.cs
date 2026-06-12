using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TestController.Persistence;

/// <summary>
/// Design-time factory used exclusively by EF Core tooling (dotnet ef migrations, dotnet ef database).
/// The WPF host project cannot be discovered at design time, so this factory provides
/// a standalone <see cref="OrchestratorDbContext"/> with a placeholder SQLite connection string.
/// At runtime, the real connection string is supplied via DI configuration.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<OrchestratorDbContext>
{
    public OrchestratorDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseSqlite("Data Source=orchestrator.db")
            .Options;

        return new OrchestratorDbContext(options);
    }
}
