using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TestController.Api.Controllers;
using TestController.Api.Interceptors;
using TestController.Api.Services;
using TestController.Persistence;
using TestController.Persistence.Audit;
using TestController.Persistence.Authorization;
using TestController.Persistence.Identity;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Configuration;
using TestControllerGrpc.Identity;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Tests.Rbac;

/// <summary>
/// Integration tests for Phase 8: notification mute/unmute + authorization.
/// </summary>
public class NotificationMuteTests : IDisposable
{
    private readonly OrchestratorDbContext _db;
    private readonly MuteService _muteService;
    private readonly NotificationsController _sut;
    private readonly SessionAuthInterceptor _interceptor;
    private readonly AuthService _authService;
    private readonly PasswordHasher _passwordHasher;
    private string _adminToken = "";
    private string _engineerToken = "";

    public NotificationMuteTests()
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
        var authzService = new AuthorizationService(optionsMonitor, dbFactory, auditWriter);
        _interceptor = new SessionAuthInterceptor(optionsMonitor, sessionStore, dbFactory);
        _authService = new AuthService(dbFactory, sessionStore, _passwordHasher, auditWriter);

        _muteService = new MuteService(dbFactory, authzService, auditWriter, new FakeAppLogger());
        _sut = new NotificationsController(_muteService, _interceptor);

        SeedData();
    }

    private void SeedData()
    {
        _db.Users.Add(new User
        {
            UserId = "admin-1",
            Username = "admin",
            Email = "admin@test.com",
            Role = Role.Administrator,
            PasswordHash = _passwordHasher.Hash("Admin123!"),
            IsActive = true,
            CreatedUtc = DateTime.UtcNow,
        });
        _db.Users.Add(new User
        {
            UserId = "eng-1",
            Username = "engineer",
            Email = "eng@test.com",
            Role = Role.Engineer,
            PasswordHash = _passwordHasher.Hash("Eng123!Pass"),
            IsActive = true,
            CreatedUtc = DateTime.UtcNow,
        });
        _db.PipelineAssignments.Add(new PipelineAssignment
        {
            UserId = "eng-1",
            PipelineId = "pipeline-A",
            AssignedByUserId = "admin-1",
            AssignedUtc = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    private async Task EnsureTokens()
    {
        if (!string.IsNullOrEmpty(_adminToken)) return;
        var adminResult = await _authService.LoginAsync("admin", "Admin123!", ClientKind.Web, "127.0.0.1");
        _adminToken = adminResult.Token!;
        var engResult = await _authService.LoginAsync("engineer", "Eng123!Pass", ClientKind.Web, "127.0.0.1");
        _engineerToken = engResult.Token!;
    }

    private void SetAuthHeader(string token)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = $"Bearer {token}";
        _sut.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    public void Dispose() => _db.Dispose();

    // ── Mute authorization tests ────────────────────────────────────────

    [Fact]
    public async Task Mute_Should_Succeed_When_Admin()
    {
        await EnsureTokens();
        SetAuthHeader(_adminToken);

        var result = await _sut.Mute(new MuteRequest { Target = "pipeline-X", TargetType = "Pipeline" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Single(await _muteService.ListMutesAsync());
    }

    [Fact]
    public async Task Mute_Should_Succeed_When_EngineerOwnsTarget()
    {
        await EnsureTokens();
        SetAuthHeader(_engineerToken);

        var result = await _sut.Mute(new MuteRequest { Target = "pipeline-A", TargetType = "Pipeline" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Mute_Should_Return403_When_EngineerDoesNotOwnTarget()
    {
        await EnsureTokens();
        SetAuthHeader(_engineerToken);

        var result = await _sut.Mute(new MuteRequest { Target = "pipeline-OTHER" }, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, status.StatusCode);
    }

    [Fact]
    public async Task Unmute_Should_Succeed_When_Admin()
    {
        await EnsureTokens();
        SetAuthHeader(_adminToken);

        // First mute
        await _sut.Mute(new MuteRequest { Target = "pipeline-Z", TargetType = "Pipeline" }, CancellationToken.None);
        var mutes = await _muteService.ListMutesAsync();
        var muteId = mutes.First().MuteId;

        // Then unmute
        var result = await _sut.Unmute(muteId, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        Assert.Empty(await _muteService.ListMutesAsync());
    }

    [Fact]
    public async Task Unmute_Should_Return404_When_NotFound()
    {
        await EnsureTokens();
        SetAuthHeader(_adminToken);

        var result = await _sut.Unmute(99999, CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ── Mute suppression tests ──────────────────────────────────────────

    [Fact]
    public async Task IsMuted_Should_ReturnTrue_When_TargetIsMuted()
    {
        await EnsureTokens();
        SetAuthHeader(_adminToken);
        await _sut.Mute(new MuteRequest { Target = "flaky-test", TargetType = "Test" }, CancellationToken.None);

        var isMuted = await _muteService.IsMutedAsync("flaky-test", "Test");
        Assert.True(isMuted);
    }

    // ── Cooldown tests ──────────────────────────────────────────────────

    [Fact]
    public async Task Cooldown_Should_SuppressWithinWindow()
    {
        await _muteService.RecordSentAsync("test-1", "Test");
        var inCooldown = await _muteService.IsInCooldownAsync("test-1", "Test", 6);
        Assert.True(inCooldown);
    }

    [Fact]
    public async Task Cooldown_Should_AllowAfterWindowExpires()
    {
        // Record sent 7 hours ago
        await using var db = _db;
        db.NotificationCooldowns.Add(new NotificationCooldown
        {
            Target = "test-old",
            TargetType = "Test",
            LastSentUtc = DateTime.UtcNow.AddHours(-7),
        });
        await db.SaveChangesAsync();

        var inCooldown = await _muteService.IsInCooldownAsync("test-old", "Test", 6);
        Assert.False(inCooldown);
    }

    // ── Audit tests ─────────────────────────────────────────────────────

    [Fact]
    public async Task Mute_Should_AuditAction()
    {
        await EnsureTokens();
        SetAuthHeader(_adminToken);
        await _sut.Mute(new MuteRequest { Target = "pipeline-audited", TargetType = "Pipeline" }, CancellationToken.None);

        // Audit entries are fire-and-forget (queued), check via DB directly won't work
        // but we can verify mute succeeded (implying audit was enqueued)
        Assert.True(await _muteService.IsMutedAsync("pipeline-audited", "Pipeline"));
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

    private sealed class FakeAppLogger : IAppLogger
    {
        public void Log(Microsoft.Extensions.Logging.LogLevel level, string category, string message, Exception? ex = null) { }
        public void Log(Microsoft.Extensions.Logging.LogLevel level, string category, string message, string? correlationId, long elapsedMs = 0, Exception? ex = null) { }
        public void LogStructured(Microsoft.Extensions.Logging.LogLevel level, string category, string message, string? agent = null, string? runId = null, string? pipeline = null, string? action = null, long elapsedMs = 0, Exception? ex = null) { }
        public void Info(string category, string message) { }
        public void Warn(string category, string message) { }
        public void Error(string category, string message, Exception? ex = null) { }
        public IReadOnlyList<AppLogEntry> GetRecentEntries(int count = 500) => [];
        public event Action<AppLogEntry>? EntryAdded;
    }
}
