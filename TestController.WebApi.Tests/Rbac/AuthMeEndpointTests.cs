using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TestController.Api.Interceptors;
using TestController.Api.Services;
using TestController.Persistence;
using TestController.Persistence.Audit;
using TestController.Persistence.Identity;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Configuration;
using TestControllerGrpc.Identity;

namespace TestController.WebApi.Tests.Rbac;

/// <summary>
/// Tests for GET /api/auth/me endpoint behavior (via service-level assertions).
/// Exercises: authenticated user info, unauthenticated rejection, first-login flag.
/// </summary>
public class AuthMeEndpointTests : IDisposable
{
    private readonly OrchestratorDbContext _db;
    private readonly AuthService _authService;
    private readonly SessionAuthInterceptor _interceptor;
    private readonly PasswordHasher _passwordHasher;
    private readonly RbacOptions _options;

    public AuthMeEndpointTests()
    {
        var dbOptions = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        _db = new OrchestratorDbContext(dbOptions);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        _options = new RbacOptions { Enabled = true };
        var optionsMonitor = new TestOptionsMonitor<RbacOptions>(_options);
        var dbFactory = new TestDbContextFactory(_db);
        _passwordHasher = new PasswordHasher();
        var sessionStore = new SessionStore(dbFactory);

        var auditWriter = new QueuedAuditWriter();
        _authService = new AuthService(dbFactory, sessionStore, _passwordHasher, auditWriter);
        _interceptor = new SessionAuthInterceptor(optionsMonitor, sessionStore, dbFactory);

        // Seed an admin user
        _db.Users.Add(new User
        {
            UserId = Guid.NewGuid().ToString("D").ToLowerInvariant(),
            Username = "admin",
            Email = "admin@test.com",
            Role = Role.Administrator,
            PasswordHash = _passwordHasher.Hash("Admin123!Pass"),
            MustChangePassword = false,
            IsActive = true,
            CreatedUtc = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Me_Should_ReturnUserInfo_When_Authenticated()
    {
        // Login to get a token
        var loginResult = await _authService.LoginAsync("admin", "Admin123!Pass", ClientKind.Web, "127.0.0.1");
        Assert.True(loginResult.Success);

        // Resolve user (simulates what AuthController.Me does)
        var user = await _interceptor.ResolveUserAsync($"Bearer {loginResult.Token}", ClientKind.Web);

        Assert.NotNull(user);
        Assert.Equal("admin", user!.DisplayName);
        Assert.Contains(Role.Administrator.ToString(), user.Roles);
        Assert.Equal(ClientKind.Web, user.ClientKind);
    }

    [Fact]
    public async Task Me_Should_ReturnNull_When_Unauthenticated()
    {
        // No token — simulates 401 response
        var user = await _interceptor.ResolveUserAsync(null, ClientKind.Web);
        Assert.Null(user);
    }

    [Fact]
    public async Task Me_Should_ReturnNull_When_InvalidToken()
    {
        // Use a well-formed base64 string that doesn't correspond to any session
        var fakeToken = Convert.ToBase64String(new byte[32]);
        var user = await _interceptor.ResolveUserAsync($"Bearer {fakeToken}", ClientKind.Web);
        Assert.Null(user);
    }

    [Fact]
    public async Task Me_Should_ReturnMustChangePassword_When_FirstLogin()
    {
        // Seed a user with MustChangePassword = true
        var userId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        _db.Users.Add(new User
        {
            UserId = userId,
            Username = "newuser",
            Email = "new@test.com",
            Role = Role.Engineer,
            PasswordHash = _passwordHasher.Hash("Initial123!Pass"),
            MustChangePassword = true,
            IsActive = true,
            CreatedUtc = DateTime.UtcNow,
        });
        _db.SaveChanges();

        // Login — should succeed but flag MustChangePassword
        var loginResult = await _authService.LoginAsync("newuser", "Initial123!Pass", ClientKind.Web, "127.0.0.1");
        Assert.True(loginResult.Success);
        Assert.True(loginResult.MustChangePassword);
    }

    [Fact]
    public async Task Me_Should_ReturnGuestInfo_When_GuestSession()
    {
        var loginResult = await _authService.LoginAsGuestAsync(ClientKind.Web, "192.168.1.1");
        Assert.True(loginResult.Success);

        var user = await _interceptor.ResolveUserAsync($"Bearer {loginResult.Token}", ClientKind.Web);
        Assert.NotNull(user);
        Assert.NotNull(user!.GuestId);
        Assert.Equal("Guest", user.DisplayName);
        Assert.Contains(Role.Guest.ToString(), user.Roles);
    }

    [Fact]
    public async Task Me_Should_ReturnCapabilities_When_AdminUser()
    {
        var loginResult = await _authService.LoginAsync("admin", "Admin123!Pass", ClientKind.Web, null);
        var user = await _interceptor.ResolveUserAsync($"Bearer {loginResult.Token}", ClientKind.Web);
        Assert.NotNull(user);

        // Verify admin gets all permissions as capabilities
        var capabilities = PermissionCatalog.GetPermissionsForRole(user!.Roles.FirstOrDefault());
        Assert.Equal(Enum.GetValues<Permission>().Length, capabilities.Count);
    }

    [Fact]
    public async Task Me_Should_ReturnNull_When_UserDeactivated()
    {
        // Login first
        var loginResult = await _authService.LoginAsync("admin", "Admin123!Pass", ClientKind.Web, null);
        Assert.True(loginResult.Success);

        // Deactivate user
        var dbUser = await _db.Users.FirstAsync(u => u.Username == "admin");
        dbUser.IsActive = false;
        await _db.SaveChangesAsync();

        // Token should no longer resolve
        var user = await _interceptor.ResolveUserAsync($"Bearer {loginResult.Token}", ClientKind.Web);
        Assert.Null(user);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

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
        public Task<OrchestratorDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
