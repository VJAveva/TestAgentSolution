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

public class AuthE2ETests : IDisposable
{
    private readonly OrchestratorDbContext _db;
    private readonly AuthService _authService;
    private readonly SessionAuthInterceptor _interceptor;
    private readonly QueuedAuditWriter _auditWriter;
    private readonly RbacOptions _options;

    public AuthE2ETests()
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
        var passwordHasher = new PasswordHasher();
        var sessionStore = new SessionStore(dbFactory);
        _auditWriter = new QueuedAuditWriter();

        _authService = new AuthService(dbFactory, sessionStore, passwordHasher);
        _interceptor = new SessionAuthInterceptor(optionsMonitor, sessionStore, dbFactory);

        // Seed an administrator
        var admin = new User
        {
            UserId = Guid.NewGuid().ToString("D").ToLowerInvariant(),
            Username = "admin",
            Email = "admin@corp.com",
            Role = Role.Administrator,
            PasswordHash = passwordHasher.Hash("Admin123!"),
            IsActive = true,
            CreatedUtc = DateTime.UtcNow,
        };
        _db.Users.Add(admin);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task FullCycle_Should_LoginAuthenticateAndLogout()
    {
        // Step 1: Login
        var loginResult = await _authService.LoginAsync("admin", "Admin123!", ClientKind.Wpf, "127.0.0.1");
        Assert.True(loginResult.Success);
        Assert.NotNull(loginResult.Token);
        Assert.Equal(Role.Administrator, loginResult.Role);

        // Step 2: Authenticated RPC — resolve user from token
        var user = await _interceptor.ResolveUserAsync($"Bearer {loginResult.Token}", ClientKind.Wpf);
        Assert.NotNull(user);
        Assert.Equal("admin", user.DisplayName);
        Assert.Contains(Role.Administrator.ToString(), user.Roles);

        // Step 3: Logout
        await _authService.LogoutAsync(loginResult.Token!);

        // Step 4: Verify token no longer works
        var userAfterLogout = await _interceptor.ResolveUserAsync($"Bearer {loginResult.Token}", ClientKind.Wpf);
        Assert.Null(userAfterLogout);
    }

    [Fact]
    public async Task Login_Should_Fail_When_InvalidPassword()
    {
        var result = await _authService.LoginAsync("admin", "WrongPassword", ClientKind.Web, null);

        Assert.False(result.Success);
        Assert.Null(result.Token);
        Assert.Equal("Invalid username or password", result.Error);
    }

    [Fact]
    public async Task Login_Should_Fail_When_UserNotFound()
    {
        var result = await _authService.LoginAsync("nonexistent", "pass", ClientKind.Web, null);

        Assert.False(result.Success);
        Assert.Equal("Invalid username or password", result.Error);
    }

    [Fact]
    public async Task LoginAsGuest_Should_ReturnGuestToken()
    {
        var result = await _authService.LoginAsGuestAsync(ClientKind.Web, "192.168.1.1");

        Assert.True(result.Success);
        Assert.NotNull(result.Token);
        Assert.Equal(Role.Guest, result.Role);
    }

    [Fact]
    public async Task ChangePassword_Should_InvalidateExistingSessions()
    {
        // Login
        var loginResult = await _authService.LoginAsync("admin", "Admin123!", ClientKind.Wpf, null);
        Assert.True(loginResult.Success);

        // Get user ID
        var user = await _db.Users.FirstAsync(u => u.Username == "admin");

        // Change password
        var (success, error) = await _authService.ChangePasswordAsync(user.UserId, "Admin123!", "NewPass456!");
        Assert.True(success);
        Assert.Null(error);

        // Old token should no longer work
        var resolved = await _interceptor.ResolveUserAsync($"Bearer {loginResult.Token}", ClientKind.Wpf);
        Assert.Null(resolved);

        // Login with new password
        var newLogin = await _authService.LoginAsync("admin", "NewPass456!", ClientKind.Wpf, null);
        Assert.True(newLogin.Success);
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
        public Task<OrchestratorDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
}
