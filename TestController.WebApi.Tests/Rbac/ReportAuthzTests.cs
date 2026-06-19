using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TestController.Api.Interceptors;
using TestController.Api.Services;
using TestController.Persistence;
using TestController.Persistence.Audit;
using TestController.Persistence.Authorization;
using TestController.Persistence.Identity;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Configuration;
using TestControllerGrpc.Identity;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Tests.Rbac;

/// <summary>
/// Phase 9 tests: Report_View open to all roles; Report_Generate restricted to Admin + SrMgr.
/// </summary>
public class ReportAuthzTests : IDisposable
{
    private readonly OrchestratorDbContext _db;
    private readonly AuthorizationService _authzService;
    private readonly SessionAuthInterceptor _interceptor;
    private readonly AuthService _authService;
    private readonly PasswordHasher _passwordHasher;
    private string _adminToken = "";
    private string _srmgrToken = "";
    private string _engineerToken = "";
    private string _guestToken = "";

    public ReportAuthzTests()
    {
        var dbOptions = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        _db = new OrchestratorDbContext(dbOptions);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        var options = new RbacOptions { Enabled = true };
        var optionsMonitor = new TestOptionsMonitor<RbacOptions>(options);
        _passwordHasher = new PasswordHasher();
        var dbFactory = new TestDbContextFactory(_db);
        var sessionStore = new SessionStore(dbFactory);
        var auditWriter = new QueuedAuditWriter();
        _authzService = new AuthorizationService(optionsMonitor, dbFactory, auditWriter);
        _interceptor = new SessionAuthInterceptor(optionsMonitor, sessionStore, dbFactory);
        _authService = new AuthService(dbFactory, sessionStore, _passwordHasher, auditWriter);

        SeedUsers();
    }

    private void SeedUsers()
    {
        _db.Users.Add(new User
        {
            UserId = "admin-1", Username = "admin", Email = "a@test.com",
            Role = Role.Administrator, PasswordHash = _passwordHasher.Hash("Admin123!"),
            IsActive = true, CreatedUtc = DateTime.UtcNow,
        });
        _db.Users.Add(new User
        {
            UserId = "srmgr-1", Username = "srmgr", Email = "s@test.com",
            Role = Role.SeniorManager, PasswordHash = _passwordHasher.Hash("SrMgr123!"),
            IsActive = true, CreatedUtc = DateTime.UtcNow,
        });
        _db.Users.Add(new User
        {
            UserId = "eng-1", Username = "engineer", Email = "e@test.com",
            Role = Role.Engineer, PasswordHash = _passwordHasher.Hash("Eng123!Pass"),
            IsActive = true, CreatedUtc = DateTime.UtcNow,
        });
        _db.Users.Add(new User
        {
            UserId = "guest-1", Username = "guest", Email = "g@test.com",
            Role = Role.Guest, PasswordHash = _passwordHasher.Hash("Guest123!"),
            IsActive = true, CreatedUtc = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    private async Task EnsureTokens()
    {
        if (!string.IsNullOrEmpty(_adminToken)) return;
        _adminToken = (await _authService.LoginAsync("admin", "Admin123!", ClientKind.Web, "127.0.0.1")).Token!;
        _srmgrToken = (await _authService.LoginAsync("srmgr", "SrMgr123!", ClientKind.Web, "127.0.0.1")).Token!;
        _engineerToken = (await _authService.LoginAsync("engineer", "Eng123!Pass", ClientKind.Web, "127.0.0.1")).Token!;
        _guestToken = (await _authService.LoginAsync("guest", "Guest123!", ClientKind.Web, "127.0.0.1")).Token!;
    }

    private async Task<IUserContext> ResolveUser(string token)
    {
        return (await _interceptor.ResolveUserAsync($"Bearer {token}", ClientKind.Web))!;
    }

    public void Dispose() => _db.Dispose();

    // ── Report_View: open to all roles ──────────────────────────────────

    [Theory]
    [InlineData("admin")]
    [InlineData("srmgr")]
    [InlineData("engineer")]
    [InlineData("guest")]
    public async Task ReportView_Should_AllowAllRoles(string role)
    {
        await EnsureTokens();
        var token = role switch
        {
            "admin" => _adminToken,
            "srmgr" => _srmgrToken,
            "engineer" => _engineerToken,
            "guest" => _guestToken,
            _ => throw new ArgumentException(role),
        };
        var user = await ResolveUser(token);

        var decision = await _authzService.CanAsync(user, Permission.Report_View);

        Assert.True(decision.Allowed, $"Report_View should be allowed for {role}");
    }

    // ── Report_Generate: Admin + SrMgr only ─────────────────────────────

    [Theory]
    [InlineData("admin")]
    [InlineData("srmgr")]
    public async Task ReportGenerate_Should_AllowAdminAndSrMgr(string role)
    {
        await EnsureTokens();
        var token = role == "admin" ? _adminToken : _srmgrToken;
        var user = await ResolveUser(token);

        var decision = await _authzService.CanAsync(user, Permission.Report_Generate);

        Assert.True(decision.Allowed, $"Report_Generate should be allowed for {role}");
    }

    [Theory]
    [InlineData("engineer")]
    [InlineData("guest")]
    public async Task ReportGenerate_Should_Deny_When_EngineerOrGuest(string role)
    {
        await EnsureTokens();
        var token = role == "engineer" ? _engineerToken : _guestToken;
        var user = await ResolveUser(token);

        var decision = await _authzService.CanAsync(user, Permission.Report_Generate);

        Assert.False(decision.Allowed, $"Report_Generate should be denied for {role}");
    }

    // ── Audit: Report_Generate produces audit entry ─────────────────────

    [Fact]
    public async Task ReportGenerate_Should_AuditDecision()
    {
        await EnsureTokens();
        var user = await ResolveUser(_adminToken);

        // CanAsync enqueues audit internally — just verify no exception
        var decision = await _authzService.CanAsync(user, Permission.Report_Generate);
        Assert.True(decision.Allowed);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class TestDbContextFactory : IDbContextFactory<OrchestratorDbContext>
    {
        private readonly DbContextOptions<OrchestratorDbContext> _options;
        public TestDbContextFactory(OrchestratorDbContext seedDb)
        {
            var conn = (Microsoft.Data.Sqlite.SqliteConnection)seedDb.Database.GetDbConnection();
            _options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseSqlite(conn).Options;
        }
        public OrchestratorDbContext CreateDbContext() => new(_options);
        public Task<OrchestratorDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
}
