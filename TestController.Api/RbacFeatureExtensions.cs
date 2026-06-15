using Microsoft.Data.Sqlite;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
using TestControllerGrpc.Identity;

namespace TestController.Api;

/// <summary>
/// Extension method grouping all RBAC DI registrations.
/// Called from both hosts (WPF App.xaml.cs and WebApi Program.cs).
/// Follows existing pattern of AddMultiIdentitySecurity() and AddControllerApi().
/// </summary>
public static class RbacFeatureExtensions
{
    /// <summary>
    /// Registers RBAC services. Only the primary host (WPF Controller) should open the SQLite database.
    /// The standalone WebApi must pass <paramref name="isPrimaryHost"/> = false to avoid DB access.
    /// </summary>
    public static IServiceCollection AddRbacFeature(this IServiceCollection services, IConfiguration configuration, bool isPrimaryHost = true)
    {
        // Configuration (always needed — both hosts read RbacOptions to know if mode is secured)
        services.Configure<RbacOptions>(configuration.GetSection(RbacOptions.SectionName));

        // Writable options for live mode switching
        var appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        services.AddSingleton<IWritableOptions<RbacOptions>>(sp =>
            new WritableOptions<RbacOptions>(
                sp.GetRequiredService<IOptionsMonitor<RbacOptions>>(),
                RbacOptions.SectionName,
                appSettingsPath));

        // SystemModeBroadcaster: SignalR-only, no DB access — safe for both hosts
        services.AddSingleton<Hubs.SystemModeBroadcaster>();

        if (isPrimaryHost)
        {
            // ── Primary host (WPF Controller): owns the SQLite database ──

            var dbPath = configuration["RBAC:DatabasePath"]
                ?? Path.Combine(AppContext.BaseDirectory, "orchestrator.db");

            if (!Path.IsPathRooted(dbPath))
                dbPath = Path.Combine(AppContext.BaseDirectory, dbPath);
            dbPath = Path.GetFullPath(dbPath);

            services.AddDbContextFactory<OrchestratorDbContext>(options =>
            {
                var connectionString = $"Data Source={dbPath};Default Timeout=5";
                options.UseSqlite(connectionString, sqlite =>
                {
                    sqlite.MigrationsAssembly(typeof(OrchestratorDbContext).Assembly.GetName().Name);
                });
                options.AddInterceptors(new SqliteConnectionInterceptor());
            });

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
            services.AddSingleton<UserService>();
            services.AddSingleton<RbacModeTransitionService>();

            // Phase 2a: Pipeline authorization
            services.AddSingleton<PipelineAuthorizationGuard>();
            services.AddSingleton<PipelineService>();
        }
        else
        {
            // ── Secondary host (standalone WebApi): NO local DB access ──
            // Uses ThrowingDbContextFactory as a safety guard — any accidental DB
            // access throws immediately instead of silently corrupting state.
            services.AddSingleton<IDbContextFactory<OrchestratorDbContext>, ThrowingDbContextFactory>();

            // Identity: no-op session store (all auth is proxied to the controller)
            services.AddSingleton<PasswordHasher>();
            services.AddSingleton<ISessionStore, NullSessionStore>();

            // Authorization: no-op (proxied via ControllerProxyService)
            services.AddSingleton<IAuthorizationService, NullAuthorizationService>();

            // Audit: no-op writer (audit is written by the controller, not the WebApi)
            services.AddSingleton<IAuditWriter, NullAuditWriter>();

            // Interceptors still needed for DI resolution by shared controllers
            services.AddSingleton<SessionAuthInterceptor>();
            services.AddSingleton<AuditLoggingInterceptor>();

            // Services: register concrete types so shared controllers can resolve them.
            // These will proxy to the controller for actual operations.
            services.AddSingleton<AuthService>();
            services.AddSingleton<UserService>();
            services.AddSingleton<RbacModeTransitionService>();

            // Phase 2a: Pipeline authorization
            services.AddSingleton<PipelineAuthorizationGuard>();
            services.AddSingleton<PipelineService>();
        }

        return services;
    }
}

/// <summary>
/// Applies pending EF Core migrations at startup, before any service touches the DB.
/// Handles the case where the DB was created by prior (now-removed) migrations or EnsureCreated
/// by baselining: if tables already exist but the current Initial migration is pending, it is
/// marked as applied without running its Up method.
/// WAL pragmas are handled per-connection by <see cref="SqliteConnectionInterceptor"/>.
/// </summary>
internal sealed class DatabaseInitializerService : Microsoft.Extensions.Hosting.IHostedService
{
    private readonly IDbContextFactory<OrchestratorDbContext> _dbFactory;
    private readonly ILogger<DatabaseInitializerService> _logger;

    public DatabaseInitializerService(
        IDbContextFactory<OrchestratorDbContext> dbFactory,
        ILogger<DatabaseInitializerService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var connStr = db.Database.GetConnectionString();
            _logger.LogInformation("RBAC database path: {ConnectionString}", connStr);

            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            if (pending.Count > 0 && await SchemaAlreadyExistsAsync(db, cancellationToken))
            {
                // Tables exist from a previous creation path (old migrations or EnsureCreated).
                // Baseline by recording all pending migrations as already applied.
                foreach (var migrationId in pending)
                {
                    await db.Database.ExecuteSqlRawAsync(
                        "INSERT OR IGNORE INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({0}, {1})",
                        [migrationId, ProductVersion()],
                        cancellationToken);
                }
            }
            else
            {
                await db.Database.MigrateAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "RBAC database migration failed — application cannot start.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Checks if the AuditEntries table already exists (proxy for "schema already created").</summary>
    private static async Task<bool> SchemaAlreadyExistsAsync(OrchestratorDbContext db, CancellationToken ct)
    {
        // Ensure the migrations history table exists so the INSERT OR IGNORE above can work.
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\" (\"MigrationId\" TEXT NOT NULL PRIMARY KEY, \"ProductVersion\" TEXT NOT NULL)",
            ct);

        var result = await db.Database.ExecuteSqlRawAsync(
            "SELECT 1 FROM sqlite_master WHERE type='table' AND name='AuditEntries'", ct);
        // ExecuteSqlRaw returns rows-affected which isn't useful here; use a raw query instead.
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='AuditEntries'";
        var count = (long)(await cmd.ExecuteScalarAsync(ct))!;
        return count > 0;
    }

    private static string ProductVersion() =>
        typeof(DbContext).Assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "10.0.0";
}

/// <summary>
/// Factory that throws if the WebApi host accidentally tries to open the SQLite database directly.
/// All DB-backed operations must be routed through ControllerProxyService.
/// </summary>
internal sealed class ThrowingDbContextFactory : IDbContextFactory<OrchestratorDbContext>
{
    public OrchestratorDbContext CreateDbContext() =>
        throw new InvalidOperationException(
            "RBAC persistence is only available on the primary controller host. " +
            "Route this operation through ControllerProxyService.");
}

/// <summary>
/// No-op session store for the standalone WebApi. Auth operations are proxied
/// to the controller; this satisfies DI resolution without DB access.
/// </summary>
internal sealed class NullSessionStore : ISessionStore
{
    public Task<Session> CreateSessionAsync(string? userId, string? guestId, ClientKind clientKind, string? ipAddress, CancellationToken ct = default)
        => throw new InvalidOperationException("Session creation must be routed through ControllerProxyService.");

    public Task<Session?> LookupByTokenAsync(string token, CancellationToken ct = default)
        => Task.FromResult<Session?>(null);

    public Task TouchAsync(string sessionId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task RevokeAsync(string sessionId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task RevokeAllForUserAsync(string userId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task RevokeAllAsync(CancellationToken ct = default)
        => Task.CompletedTask;
}

/// <summary>
/// No-op authorization service for the standalone WebApi. Always allows —
/// the real authorization check happens on the controller when proxied.
/// </summary>
internal sealed class NullAuthorizationService : TestControllerGrpc.Authorization.IAuthorizationService
{
    public Task<AuthDecision> CanAsync(IUserContext user, Permission permission, string? resourceId = null, CancellationToken ct = default)
        => Task.FromResult(AuthDecision.Allow("proxy-bypass"));
}

/// <summary>
/// No-op audit writer for the standalone WebApi. Audit entries are written
/// by the controller; the WebApi does not need its own audit drain.
/// </summary>
internal sealed class NullAuditWriter : IAuditWriter
{
    public void Enqueue(AuditEntry entry) { /* no-op */ }
}

internal sealed class SqliteConnectionInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor
{
    public override void ConnectionOpened(
        System.Data.Common.DbConnection connection,
        Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEndEventData eventData)
    {
        OrchestratorDbContextExtensions.ApplyPragmas((SqliteConnection)connection);
    }

    public override async Task ConnectionOpenedAsync(
        System.Data.Common.DbConnection connection,
        Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        OrchestratorDbContextExtensions.ApplyPragmas((SqliteConnection)connection);
        await Task.CompletedTask;
    }
}
