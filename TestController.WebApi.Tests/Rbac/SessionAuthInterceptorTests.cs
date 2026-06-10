using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TestController.Api.Interceptors;
using TestController.Persistence;
using TestController.Persistence.Identity;
using TestControllerGrpc.Configuration;
using TestControllerGrpc.Identity;

namespace TestController.WebApi.Tests.Rbac;

public class SessionAuthInterceptorTests : IDisposable
{
    private readonly OrchestratorDbContext _db;
    private readonly SessionStore _sessionStore;
    private readonly SessionAuthInterceptor _sut;
    private readonly RbacOptions _options;

    public SessionAuthInterceptorTests()
    {
        var dbOptions = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        _db = new OrchestratorDbContext(dbOptions);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        _options = new RbacOptions { Enabled = false };
        var optionsMonitor = new TestOptionsMonitor<RbacOptions>(_options);
        var dbFactory = new TestDbContextFactory(_db);

        _sessionStore = new SessionStore(dbFactory);
        _sut = new SessionAuthInterceptor(optionsMonitor, _sessionStore, dbFactory);
    }

    public void Dispose() => _db.Dispose();

    // ── Default Mode Tests ───────────────────────────────────────────────

    [Fact]
    public async Task ResolveUserAsync_Should_ReturnDefaultUser_When_DefaultModeNoToken()
    {
        var user = await _sut.ResolveUserAsync(null, ClientKind.Wpf);

        Assert.NotNull(user);
        Assert.Equal(DefaultUser.UserId.ToString("D"), user.UserId);
        Assert.Equal("Default user", user.DisplayName);
        Assert.Equal(ClientKind.Wpf, user.ClientKind);
    }

    [Fact]
    public async Task ResolveUserAsync_Should_ReturnDefaultWebUser_When_DefaultModeWebClient()
    {
        var user = await _sut.ResolveUserAsync(null, ClientKind.Web);

        Assert.NotNull(user);
        Assert.Equal(DefaultUser.UserId.ToString("D"), user.UserId);
        Assert.Equal(ClientKind.Web, user.ClientKind);
    }

    // ── Secured Mode Tests ───────────────────────────────────────────────

    [Fact]
    public async Task ResolveUserAsync_Should_ReturnNull_When_SecuredModeNoToken()
    {
        _options.Enabled = true;

        var user = await _sut.ResolveUserAsync(null, ClientKind.Wpf);

        Assert.Null(user);
    }

    [Fact]
    public async Task ResolveUserAsync_Should_ReturnNull_When_SecuredModeInvalidToken()
    {
        _options.Enabled = true;

        var user = await _sut.ResolveUserAsync("Bearer invalid-token-data", ClientKind.Web);

        Assert.Null(user);
    }

    [Fact]
    public async Task ResolveUserAsync_Should_ReturnUser_When_SecuredModeValidToken()
    {
        _options.Enabled = true;

        // Seed a user
        var testUser = new User
        {
            UserId = Guid.NewGuid().ToString("D").ToLowerInvariant(),
            Username = "testadmin",
            Email = "test@corp.com",
            Role = Role.Administrator,
            PasswordHash = "hashed",
            IsActive = true,
            CreatedUtc = DateTime.UtcNow,
        };
        _db.Users.Add(testUser);
        await _db.SaveChangesAsync();

        // Create a session
        var session = await _sessionStore.CreateSessionAsync(testUser.UserId, null, ClientKind.Wpf, "127.0.0.1");
        var token = SessionStore.TokenToString(session.TokenHash);

        var user = await _sut.ResolveUserAsync($"Bearer {token}", ClientKind.Wpf);

        Assert.NotNull(user);
        Assert.Equal(testUser.UserId, user.UserId);
        Assert.Equal("testadmin", user.DisplayName);
        Assert.Contains(Role.Administrator.ToString(), user.Roles);
    }

    [Fact]
    public async Task ResolveUserAsync_Should_ReturnNull_When_SecuredModeRevokedSession()
    {
        _options.Enabled = true;

        var testUser = new User
        {
            UserId = Guid.NewGuid().ToString("D").ToLowerInvariant(),
            Username = "revoked-user",
            Email = "revoked@corp.com",
            Role = Role.Engineer,
            PasswordHash = "hashed",
            IsActive = true,
            CreatedUtc = DateTime.UtcNow,
        };
        _db.Users.Add(testUser);
        await _db.SaveChangesAsync();

        var session = await _sessionStore.CreateSessionAsync(testUser.UserId, null, ClientKind.Web, null);
        var token = SessionStore.TokenToString(session.TokenHash);

        // Revoke the session
        await _sessionStore.RevokeAsync(session.SessionId);

        var user = await _sut.ResolveUserAsync($"Bearer {token}", ClientKind.Web);

        Assert.Null(user);
    }

    [Fact]
    public async Task ResolveUserAsync_Should_ReturnDefaultUser_When_ModeSwitchesMidSession()
    {
        // Start in Secured mode, switch to Default mid-call
        _options.Enabled = true;
        var user1 = await _sut.ResolveUserAsync(null, ClientKind.Wpf);
        Assert.Null(user1); // No token in secured mode → null

        // Switch to Default mode
        _options.Enabled = false;
        var user2 = await _sut.ResolveUserAsync(null, ClientKind.Wpf);
        Assert.NotNull(user2); // Default mode → synthetic user
        Assert.Equal("Default user", user2.DisplayName);
    }

    // ── Helper classes ───────────────────────────────────────────────────

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private readonly T _value;
        public TestOptionsMonitor(T value) => _value = value;
        public T CurrentValue => _value;
        public T Get(string? name) => _value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class TestDbContextFactory : IDbContextFactory<OrchestratorDbContext>
    {
        private readonly DbContextOptions<OrchestratorDbContext> _options;

        public TestDbContextFactory(OrchestratorDbContext seedDb)
        {
            var conn = (Microsoft.Data.Sqlite.SqliteConnection)seedDb.Database.GetDbConnection();
            _options = new DbContextOptionsBuilder<OrchestratorDbContext>()
                .UseSqlite(conn)
                .Options;
        }

        public OrchestratorDbContext CreateDbContext() => new(_options);
        public Task<OrchestratorDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
}
