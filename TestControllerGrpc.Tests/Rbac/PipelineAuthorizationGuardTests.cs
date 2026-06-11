using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TestController.Api.Services;
using TestController.Persistence;
using TestController.Persistence.Audit;
using TestController.Persistence.Authorization;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Configuration;
using TestControllerGrpc.Identity;

namespace TestControllerGrpc.Tests.Rbac;

public class PipelineAuthorizationGuardTests : IDisposable
{
    private readonly OrchestratorDbContext _db;
    private readonly QueuedAuditWriter _auditWriter;
    private readonly AuthorizationService _authzService;
    private readonly PipelineAuthorizationGuard _guard;
    private readonly RbacOptions _options;

    public PipelineAuthorizationGuardTests()
    {
        var dbOptions = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        _db = new OrchestratorDbContext(dbOptions);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        _options = new RbacOptions { Enabled = true };
        var optionsMonitor = new TestOptionsMonitor<RbacOptions>(_options);
        _auditWriter = new QueuedAuditWriter();

        var dbFactory = new TestDbContextFactory(_db);
        _authzService = new AuthorizationService(optionsMonitor, dbFactory, _auditWriter);
        _guard = new PipelineAuthorizationGuard(_authzService);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task AuthorizeAsync_Should_PassThrough_When_AdminUser()
    {
        var admin = new SyntheticUserContext(
            userId: "admin-1", displayName: "Admin",
            clientKind: ClientKind.Wpf,
            roles: [Role.Administrator.ToString()]);

        // Should not throw
        await _guard.AuthorizeAsync(admin, Permission.Pipeline_Trigger, "pipeline-A");
    }

    [Fact]
    public async Task AuthorizeAsync_Should_PassThrough_When_SrMgrUser()
    {
        var srmgr = new SyntheticUserContext(
            userId: "srmgr-1", displayName: "SrMgr",
            clientKind: ClientKind.Wpf,
            roles: [Role.SeniorManager.ToString()]);

        // SrMgr can trigger any pipeline
        await _guard.AuthorizeAsync(srmgr, Permission.Pipeline_Trigger, "any-pipeline");
    }

    [Fact]
    public async Task AuthorizeAsync_Should_PassThrough_When_EngineerAssigned()
    {
        var engineer = new SyntheticUserContext(
            userId: "eng-1", displayName: "Engineer",
            clientKind: ClientKind.Wpf,
            roles: [Role.Engineer.ToString()],
            assignedPipelineIds: new HashSet<string> { "pipeline-A", "pipeline-B" });

        await _guard.AuthorizeAsync(engineer, Permission.Pipeline_Trigger, "pipeline-A");
    }

    [Fact]
    public async Task AuthorizeAsync_Should_Throw_When_EngineerNotAssigned()
    {
        var engineer = new SyntheticUserContext(
            userId: "eng-1", displayName: "Engineer",
            clientKind: ClientKind.Wpf,
            roles: [Role.Engineer.ToString()],
            assignedPipelineIds: new HashSet<string> { "pipeline-A" });

        var ex = await Assert.ThrowsAsync<PipelineAuthorizationDeniedException>(
            () => _guard.AuthorizeAsync(engineer, Permission.Pipeline_Trigger, "pipeline-C"));

        Assert.Equal("no-assignment", ex.ReasonCode);
        Assert.Equal(Permission.Pipeline_Trigger, ex.Permission);
        Assert.Equal("pipeline-C", ex.ResourceId);
    }

    [Fact]
    public async Task AuthorizeAsync_Should_Throw_When_GuestTriesWrite()
    {
        var guest = new SyntheticUserContext(
            userId: "guest-1", displayName: "Guest",
            clientKind: ClientKind.Web,
            roles: [Role.Guest.ToString()],
            guestId: "guest-1");

        var ex = await Assert.ThrowsAsync<PipelineAuthorizationDeniedException>(
            () => _guard.AuthorizeAsync(guest, Permission.Pipeline_Trigger, "pipeline-A"));

        Assert.Equal("guest-readonly", ex.ReasonCode);
    }

    [Fact]
    public async Task AuthorizeAsync_Should_PassThrough_When_DefaultModeWpf()
    {
        // Switch to Default mode
        _options.Enabled = false;

        var user = DefaultUser.ForClient(ClientKind.Wpf);

        // Should not throw — Default mode WPF always allows
        await _guard.AuthorizeAsync(user, Permission.Pipeline_Trigger, "any-pipeline");
        await _guard.AuthorizeAsync(user, Permission.Pipeline_Cancel, "any-pipeline");
    }

    [Fact]
    public async Task AuthorizeAsync_Should_Throw_When_DefaultModeWebWrite()
    {
        // Default mode — Web can only read
        _options.Enabled = false;

        var user = DefaultUser.ForClient(ClientKind.Web);

        var ex = await Assert.ThrowsAsync<PipelineAuthorizationDeniedException>(
            () => _guard.AuthorizeAsync(user, Permission.Pipeline_Trigger, "pipeline-A"));

        Assert.Equal("default-mode-web-readonly", ex.ReasonCode);
    }

    [Fact]
    public async Task AuthorizeAsync_Should_ThrowCorrectException_When_EngineerNoRole()
    {
        var engineer = new SyntheticUserContext(
            userId: "eng-1", displayName: "Engineer",
            clientKind: ClientKind.Wpf,
            roles: [Role.Engineer.ToString()],
            assignedPipelineIds: new HashSet<string>());

        var ex = await Assert.ThrowsAsync<PipelineAuthorizationDeniedException>(
            () => _guard.AuthorizeAsync(engineer, Permission.User_Create));

        Assert.Equal("no-role", ex.ReasonCode);
    }

    [Fact]
    public async Task AuthorizeAsync_Should_PassThrough_When_CancelAuthorizedForAssignedPipeline()
    {
        var engineer = new SyntheticUserContext(
            userId: "eng-1", displayName: "Engineer",
            clientKind: ClientKind.Wpf,
            roles: [Role.Engineer.ToString()],
            assignedPipelineIds: new HashSet<string> { "pipeline-X" });

        await _guard.AuthorizeAsync(engineer, Permission.Pipeline_Cancel, "pipeline-X");
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
