using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TestController.Api.SystemMode;
using TestController.Persistence;
using TestController.Persistence.Audit;
using TestController.Persistence.Identity;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Configuration;
using TestControllerGrpc.Identity;

namespace TestControllerGrpc.Tests.Rbac;

public class RbacModeTransitionServiceTests : IDisposable
{
    private readonly OrchestratorDbContext _db;
    private readonly RbacModeTransitionService _sut;
    private readonly RbacOptions _options;
    private readonly TestWritableOptions _writableOptions;
    private readonly SessionStore _sessionStore;

    public RbacModeTransitionServiceTests()
    {
        var dbOptions = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        _db = new OrchestratorDbContext(dbOptions);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        _options = new RbacOptions { Enabled = false };
        _writableOptions = new TestWritableOptions(_options);
        var dbFactory = new TestDbContextFactory(_db);
        _sessionStore = new SessionStore(dbFactory);
        var auditWriter = new QueuedAuditWriter();
        var passwordHasher = new PasswordHasher();

        _sut = new RbacModeTransitionService(dbFactory, _writableOptions, _sessionStore, passwordHasher, auditWriter);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task SwitchToSecuredAsync_Should_CreateAdminAndEnableRbac_When_DefaultMode()
    {
        var (success, error) = await _sut.SwitchToSecuredAsync("admin", "admin@corp.com", "SecurePass123!");

        Assert.True(success);
        Assert.Null(error);
        Assert.True(_options.Enabled);

        var admin = await _db.Users.FirstOrDefaultAsync(u => u.Username == "admin");
        Assert.NotNull(admin);
        Assert.Equal(Role.Administrator, admin.Role);
        Assert.True(admin.IsActive);
    }

    [Fact]
    public async Task SwitchToSecuredAsync_Should_BeIdempotent_When_AlreadySecured()
    {
        _options.Enabled = true;

        var (success, error) = await _sut.SwitchToSecuredAsync("admin2", "admin2@corp.com", "SecurePass123!");

        Assert.True(success);
        Assert.Null(error);
        // No new user created since already in Secured mode
    }

    [Fact]
    public async Task SwitchToDefaultAsync_Should_RevokeSessionsAndArchiveUsers_When_SecuredMode()
    {
        // Setup: switch to Secured first
        await _sut.SwitchToSecuredAsync("admin", "admin@corp.com", "SecurePass123!");
        Assert.True(_options.Enabled);

        // Create a session
        var adminUser = await _db.Users.FirstAsync(u => u.Username == "admin");
        await _sessionStore.CreateSessionAsync(adminUser.UserId, null, ClientKind.Wpf, null);

        // Act: switch to Default
        var actor = new SyntheticUserContext(adminUser.UserId, "admin", ClientKind.Wpf, [Role.Administrator.ToString()]);
        var (success, error) = await _sut.SwitchToDefaultAsync(actor);

        Assert.True(success);
        Assert.Null(error);
        Assert.False(_options.Enabled);

        // Sessions revoked
        var activeSessions = await _db.Sessions.Where(s => s.RevokedUtc == null).CountAsync();
        Assert.Equal(0, activeSessions);

        // Users archived
        var activeUsers = await _db.Users.Where(u => u.IsActive).CountAsync();
        Assert.Equal(0, activeUsers);
    }

    [Fact]
    public async Task SwitchToDefaultAsync_Should_BeIdempotent_When_AlreadyDefault()
    {
        var actor = new SyntheticUserContext("admin-id", "admin", ClientKind.Wpf, [Role.Administrator.ToString()]);
        var (success, error) = await _sut.SwitchToDefaultAsync(actor);

        Assert.True(success);
        Assert.Null(error);
    }

    [Fact]
    public async Task SwitchToSecuredAsync_Should_Fail_When_DuplicateUsername()
    {
        // Create user manually
        _db.Users.Add(new User
        {
            UserId = Guid.NewGuid().ToString("D").ToLowerInvariant(),
            Username = "admin",
            Email = "existing@corp.com",
            Role = Role.Engineer,
            PasswordHash = "hash",
            IsActive = true,
            CreatedUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var (success, error) = await _sut.SwitchToSecuredAsync("admin", "new@corp.com", "Pass123!");

        Assert.False(success);
        Assert.Contains("already exists", error);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private sealed class TestWritableOptions : IWritableOptions<RbacOptions>
    {
        private readonly RbacOptions _value;
        public TestWritableOptions(RbacOptions value) => _value = value;
        public RbacOptions Value => _value;
        public void Update(Action<RbacOptions> applyChanges) => applyChanges(_value);
    }

    private sealed class TestDbContextFactory : IDbContextFactory<OrchestratorDbContext>
    {
        private readonly DbContextOptions<OrchestratorDbContext> _options;
        private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;

        public TestDbContextFactory(OrchestratorDbContext seedDb)
        {
            // Share the same in-memory connection
            _connection = (Microsoft.Data.Sqlite.SqliteConnection)seedDb.Database.GetDbConnection();
            _options = new DbContextOptionsBuilder<OrchestratorDbContext>()
                .UseSqlite(_connection)
                .Options;
        }

        public OrchestratorDbContext CreateDbContext() => new(_options);
        public Task<OrchestratorDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
}
