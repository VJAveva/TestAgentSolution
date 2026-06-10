using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestController.Api.Interceptors;
using TestController.Api.Services;
using TestController.Api.SystemMode;
using TestController.Persistence;
using TestController.Persistence.Audit;
using TestController.Persistence.Authorization;
using TestController.Persistence.Identity;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Configuration;

namespace TestController.Api;

/// <summary>
/// Extension method grouping all RBAC DI registrations.
/// Called from both hosts (WPF App.xaml.cs and WebApi Program.cs).
/// Follows existing pattern of AddMultiIdentitySecurity() and AddControllerApi().
/// </summary>
public static class RbacFeatureExtensions
{
    public static IServiceCollection AddRbacFeature(this IServiceCollection services, IConfiguration configuration)
    {
        // Configuration
        services.Configure<RbacOptions>(configuration.GetSection(RbacOptions.SectionName));

        // Writable options for live mode switching
        var appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        services.AddSingleton<IWritableOptions<RbacOptions>>(sp =>
            new WritableOptions<RbacOptions>(
                sp.GetRequiredService<IOptionsMonitor<RbacOptions>>(),
                RbacOptions.SectionName,
                appSettingsPath));

        // EF Core + SQLite (Scoped DbContext accessed via IDbContextFactory from Singletons)
        var dbPath = configuration["RBAC:DatabasePath"]
            ?? Path.Combine(AppContext.BaseDirectory, "orchestrator.db");

        services.AddDbContextFactory<OrchestratorDbContext>(options =>
        {
            var connectionString = $"Data Source={dbPath}";
            options.UseSqlite(connectionString, sqlite =>
            {
                sqlite.MigrationsAssembly(typeof(OrchestratorDbContext).Assembly.GetName().Name);
            });
        });

        // Ensure database is created and pragmas are applied
        services.AddHostedService<DatabaseInitializerService>();

        // Identity
        services.AddSingleton<PasswordHasher>();
        services.AddSingleton<ISessionStore, SessionStore>();

        // Authorization
        services.AddSingleton<IAuthorizationService, AuthorizationService>();

        // Audit (fire-and-forget pattern)
        services.AddSingleton<QueuedAuditWriter>();
        services.AddSingleton<IAuditWriter>(sp => sp.GetRequiredService<QueuedAuditWriter>());
        services.AddHostedService<AuditDrainWorker>();

        // Interceptors
        services.AddSingleton<SessionAuthInterceptor>();
        services.AddSingleton<AuditLoggingInterceptor>();

        // Services
        services.AddSingleton<AuthService>();
        services.AddSingleton<RbacModeTransitionService>();
        services.AddSingleton<Hubs.SystemModeBroadcaster>();

        return services;
    }
}

/// <summary>
/// Ensures the SQLite database exists, applies migrations, and sets WAL pragmas.
/// </summary>
internal sealed class DatabaseInitializerService : Microsoft.Extensions.Hosting.IHostedService
{
    private readonly IDbContextFactory<OrchestratorDbContext> _dbFactory;

    public DatabaseInitializerService(IDbContextFactory<OrchestratorDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);

        // Apply WAL pragmas
        var conn = db.Database.GetDbConnection() as SqliteConnection;
        if (conn is not null)
        {
            await conn.OpenAsync(cancellationToken);
            OrchestratorDbContextExtensions.ApplyPragmas(conn);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
