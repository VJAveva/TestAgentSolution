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
/// Integration tests for pipeline trigger/cancel authorization.
/// Per phase-2a-context.md exit criteria.
/// </summary>
public class PipelineTriggerAuthzTests : IDisposable
{
    private readonly OrchestratorDbContext _db;
    private readonly PipelineAuthorizationGuard _guard;
    private readonly PipelineService _pipelineService;
    private readonly SessionAuthInterceptor _interceptor;
    private readonly AuthService _authService;
    private readonly PasswordHasher _passwordHasher;
    private readonly RbacOptions _options;

    public PipelineTriggerAuthzTests()
    {
        var dbOptions = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        _db = new OrchestratorDbContext(dbOptions);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        _options = new RbacOptions { Enabled = true };
        var optionsMonitor = new TestOptionsMonitor<RbacOptions>(_options);
        _passwordHasher = new PasswordHasher();
        var dbFactory = new TestDbContextFactory(_db);
        var sessionStore = new SessionStore(dbFactory);
        var auditWriter = new QueuedAuditWriter();

        var authzService = new AuthorizationService(optionsMonitor, dbFactory, auditWriter);
        _guard = new PipelineAuthorizationGuard(authzService);
        _pipelineService = new PipelineService(_guard, new FakeAppLogger(), auditWriter, null);
        _authService = new AuthService(dbFactory, sessionStore, _passwordHasher, auditWriter);
        _interceptor = new SessionAuthInterceptor(optionsMonitor, sessionStore, dbFactory);

        // Seed users
        SeedData();
    }

    private void SeedData()
    {
        // Admin
        _db.Users.Add(new User
        {
            UserId = "admin-1",
            Username = "admin",
            Email = "admin@test.com",
            Role = Role.Administrator,
            PasswordHash = _passwordHasher.Hash("Admin123!Pass"),
            IsActive = true,
            CreatedUtc = DateTime.UtcNow,
        });

        // Senior Manager
        _db.Users.Add(new User
        {
            UserId = "srmgr-1",
            Username = "priya.s",
            Email = "priya@test.com",
            Role = Role.SeniorManager,
            PasswordHash = _passwordHasher.Hash("SrMgr123!Pass"),
            IsActive = true,
            CreatedUtc = DateTime.UtcNow,
        });

        // Engineer (assigned to pipeline-A only)
        _db.Users.Add(new User
        {
            UserId = "eng-1",
            Username = "ravi.kumar",
            Email = "ravi@test.com",
            Role = Role.Engineer,
            PasswordHash = _passwordHasher.Hash("Eng123!Pass"),
            IsActive = true,
            CreatedUtc = DateTime.UtcNow,
        });

        _db.PipelineAssignments.Add(new PipelineAssignment
        {
            UserId = "eng-1",
            PipelineId = "pipeline-A",
            AssignedUtc = DateTime.UtcNow,
            AssignedByUserId = "admin-1",
        });

        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Trigger_Should_Succeed_When_EngineerAssigned()
    {
        var loginResult = await _authService.LoginAsync("ravi.kumar", "Eng123!Pass", ClientKind.Web, "127.0.0.1");
        var user = await _interceptor.ResolveUserAsync($"Bearer {loginResult.Token}", ClientKind.Web);

        // Should not throw — engineer is assigned to pipeline-A
        await _pipelineService.AuthorizeTriggerAsync(user!, "pipeline-A");
    }

    [Fact]
    public async Task Trigger_Should_Fail_When_EngineerNotAssigned()
    {
        var loginResult = await _authService.LoginAsync("ravi.kumar", "Eng123!Pass", ClientKind.Web, "127.0.0.1");
        var user = await _interceptor.ResolveUserAsync($"Bearer {loginResult.Token}", ClientKind.Web);

        var ex = await Assert.ThrowsAsync<PipelineAuthorizationDeniedException>(
            () => _pipelineService.AuthorizeTriggerAsync(user!, "pipeline-C"));

        Assert.Equal("no-assignment", ex.ReasonCode);
    }

    [Fact]
    public async Task Trigger_Should_Succeed_When_SrMgrAnyPipeline()
    {
        var loginResult = await _authService.LoginAsync("priya.s", "SrMgr123!Pass", ClientKind.Web, "127.0.0.1");
        var user = await _interceptor.ResolveUserAsync($"Bearer {loginResult.Token}", ClientKind.Web);

        // SrMgr can trigger any pipeline
        await _pipelineService.AuthorizeTriggerAsync(user!, "pipeline-C");
        await _pipelineService.AuthorizeTriggerAsync(user!, "pipeline-Z");
    }

    [Fact]
    public async Task Trigger_Should_Fail_When_GuestUser()
    {
        var loginResult = await _authService.LoginAsGuestAsync(ClientKind.Web, "192.168.1.1");
        var user = await _interceptor.ResolveUserAsync($"Bearer {loginResult.Token}", ClientKind.Web);

        var ex = await Assert.ThrowsAsync<PipelineAuthorizationDeniedException>(
            () => _pipelineService.AuthorizeTriggerAsync(user!, "pipeline-A"));

        Assert.Equal("guest-readonly", ex.ReasonCode);
    }

    [Fact]
    public async Task Cancel_Should_Succeed_When_EngineerAssigned()
    {
        var loginResult = await _authService.LoginAsync("ravi.kumar", "Eng123!Pass", ClientKind.Web, "127.0.0.1");
        var user = await _interceptor.ResolveUserAsync($"Bearer {loginResult.Token}", ClientKind.Web);

        await _pipelineService.AuthorizeCancelAsync(user!, "pipeline-A");
    }

    [Fact]
    public async Task Cancel_Should_Fail_When_EngineerNotAssigned()
    {
        var loginResult = await _authService.LoginAsync("ravi.kumar", "Eng123!Pass", ClientKind.Web, "127.0.0.1");
        var user = await _interceptor.ResolveUserAsync($"Bearer {loginResult.Token}", ClientKind.Web);

        var ex = await Assert.ThrowsAsync<PipelineAuthorizationDeniedException>(
            () => _pipelineService.AuthorizeCancelAsync(user!, "pipeline-C"));

        Assert.Equal("no-assignment", ex.ReasonCode);
    }

    [Fact]
    public async Task Trigger_Should_AlwaysSucceed_When_DefaultModeWpf()
    {
        _options.Enabled = false;

        var user = DefaultUser.ForClient(ClientKind.Wpf);

        // Default mode WPF: all triggers succeed (no auth popup, no denial)
        await _pipelineService.AuthorizeTriggerAsync(user, "any-pipeline");
        await _pipelineService.AuthorizeCancelAsync(user, "any-pipeline");
    }

    [Fact]
    public async Task Trigger_Should_Fail_When_DefaultModeWebWrite()
    {
        _options.Enabled = false;

        var user = DefaultUser.ForClient(ClientKind.Web);

        var ex = await Assert.ThrowsAsync<PipelineAuthorizationDeniedException>(
            () => _pipelineService.AuthorizeTriggerAsync(user, "pipeline-A"));

        Assert.Equal("default-mode-web-readonly", ex.ReasonCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private sealed class FakeAppLogger : IAppLogger
    {
        public void Log(Microsoft.Extensions.Logging.LogLevel level, string category, string message, Exception? ex = null) { }
        public void Log(Microsoft.Extensions.Logging.LogLevel level, string category, string message, string? correlationId, long elapsedMs = 0, Exception? ex = null) { }
        public void Info(string category, string message) { }
        public void Warn(string category, string message) { }
        public void Error(string category, string message, Exception? ex = null) { }
        public IReadOnlyList<AppLogEntry> GetRecentEntries(int count = 500) => [];
        public event Action<AppLogEntry>? EntryAdded;
    }

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
