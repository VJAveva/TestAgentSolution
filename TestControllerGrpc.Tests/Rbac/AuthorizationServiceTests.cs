using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TestController.Persistence;
using TestController.Persistence.Audit;
using TestController.Persistence.Authorization;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Configuration;
using TestControllerGrpc.Identity;

namespace TestControllerGrpc.Tests.Rbac;

public class AuthorizationServiceTests : IDisposable
{
    private readonly OrchestratorDbContext _db;
    private readonly QueuedAuditWriter _auditWriter;
    private readonly AuthorizationService _sut;
    private readonly RbacOptions _options;

    public AuthorizationServiceTests()
    {
        var dbOptions = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        _db = new OrchestratorDbContext(dbOptions);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        _options = new RbacOptions { Enabled = false };
        var optionsMonitor = new TestOptionsMonitor<RbacOptions>(_options);
        _auditWriter = new QueuedAuditWriter();

        var dbFactory = new TestDbContextFactory(_db);
        _sut = new AuthorizationService(optionsMonitor, dbFactory, _auditWriter);
    }

    public void Dispose() => _db.Dispose();

    // ── Default Mode Tests ───────────────────────────────────────────────

    [Fact]
    public async Task CanAsync_Should_AllowAll_When_DefaultModeAndWpfClient()
    {
        var user = DefaultUser.ForClient(ClientKind.Wpf);

        foreach (var perm in Enum.GetValues<Permission>())
        {
            var result = await _sut.CanAsync(user, perm, "test-pipeline");
            Assert.True(result.Allowed, $"Expected Allow for {perm} in Default mode + WPF");
            Assert.Equal("default-mode-wpf", result.ReasonCode);
        }
    }

    [Theory]
    [InlineData(Permission.Pipeline_View)]
    [InlineData(Permission.Report_View)]
    public async Task CanAsync_Should_AllowRead_When_DefaultModeAndWebClient(Permission perm)
    {
        var user = DefaultUser.ForClient(ClientKind.Web);
        var result = await _sut.CanAsync(user, perm);
        Assert.True(result.Allowed);
        Assert.Equal("default-mode-web-read", result.ReasonCode);
    }

    [Theory]
    [InlineData(Permission.Pipeline_Trigger)]
    [InlineData(Permission.Pipeline_Cancel)]
    [InlineData(Permission.Pipeline_TriggerAll)]
    [InlineData(Permission.User_Create)]
    [InlineData(Permission.Audit_View)]
    public async Task CanAsync_Should_DenyWrite_When_DefaultModeAndWebClient(Permission perm)
    {
        var user = DefaultUser.ForClient(ClientKind.Web);
        var result = await _sut.CanAsync(user, perm);
        Assert.False(result.Allowed);
        Assert.Equal("default-mode-web-readonly", result.ReasonCode);
    }

    // ── Secured Mode Tests ───────────────────────────────────────────────

    [Fact]
    public async Task CanAsync_Should_AllowAll_When_SecuredModeAndAdminRole()
    {
        _options.Enabled = true;
        var admin = new SyntheticUserContext("admin-id", "Admin", ClientKind.Wpf, [Role.Administrator.ToString()]);

        foreach (var perm in Enum.GetValues<Permission>())
        {
            var result = await _sut.CanAsync(admin, perm, "any-resource");
            Assert.True(result.Allowed, $"Expected Allow for Admin on {perm}");
            Assert.Equal("admin", result.ReasonCode);
        }
    }

    [Theory]
    [InlineData(Permission.Pipeline_View)]
    [InlineData(Permission.Report_View)]
    public async Task CanAsync_Should_AllowRead_When_SecuredModeAndGuestRole(Permission perm)
    {
        _options.Enabled = true;
        var guest = new SyntheticUserContext("guest-id", "Guest", ClientKind.Web, [Role.Guest.ToString()], guestId: "guest-id");

        var result = await _sut.CanAsync(guest, perm);
        Assert.True(result.Allowed);
    }

    [Theory]
    [InlineData(Permission.Pipeline_Trigger)]
    [InlineData(Permission.Pipeline_Cancel)]
    [InlineData(Permission.User_Create)]
    public async Task CanAsync_Should_Deny_When_SecuredModeAndGuestRole(Permission perm)
    {
        _options.Enabled = true;
        var guest = new SyntheticUserContext("guest-id", "Guest", ClientKind.Web, [Role.Guest.ToString()], guestId: "guest-id");

        var result = await _sut.CanAsync(guest, perm, "pipeline-1");
        Assert.False(result.Allowed);
        Assert.Equal("guest-readonly", result.ReasonCode);
    }

    [Fact]
    public async Task CanAsync_Should_AllowPipelineOps_When_SecuredModeAndSeniorManager()
    {
        _options.Enabled = true;
        var srMgr = new SyntheticUserContext("mgr-id", "SrMgr", ClientKind.Web, [Role.SeniorManager.ToString()]);

        var result = await _sut.CanAsync(srMgr, Permission.Pipeline_Trigger, "pipeline-1");
        Assert.True(result.Allowed);
        Assert.Equal("sr-mgr", result.ReasonCode);
    }

    [Theory]
    [InlineData(Permission.User_Create)]
    [InlineData(Permission.User_Delete)]
    [InlineData(Permission.Pipeline_Enable)]
    [InlineData(Permission.Audit_View)]
    public async Task CanAsync_Should_Deny_When_SecuredModeAndSeniorManagerOnAdminPerms(Permission perm)
    {
        _options.Enabled = true;
        var srMgr = new SyntheticUserContext("mgr-id", "SrMgr", ClientKind.Web, [Role.SeniorManager.ToString()]);

        var result = await _sut.CanAsync(srMgr, perm);
        Assert.False(result.Allowed);
        Assert.Equal("no-role", result.ReasonCode);
    }

    [Fact]
    public async Task CanAsync_Should_AllowTrigger_When_SecuredModeAndEngineerAssigned()
    {
        _options.Enabled = true;
        var assignedPipelines = new HashSet<string> { "pipeline-1" };
        var engineer = new SyntheticUserContext("eng-id", "Engineer", ClientKind.Web, [Role.Engineer.ToString()], assignedPipelines);

        var result = await _sut.CanAsync(engineer, Permission.Pipeline_Trigger, "pipeline-1");
        Assert.True(result.Allowed);
        Assert.Equal("engineer-assigned", result.ReasonCode);
    }

    [Fact]
    public async Task CanAsync_Should_DenyTrigger_When_SecuredModeAndEngineerNotAssigned()
    {
        _options.Enabled = true;
        var engineer = new SyntheticUserContext("eng-id", "Engineer", ClientKind.Web, [Role.Engineer.ToString()], new HashSet<string>());

        var result = await _sut.CanAsync(engineer, Permission.Pipeline_Trigger, "pipeline-2");
        Assert.False(result.Allowed);
        Assert.Equal("no-assignment", result.ReasonCode);
    }

    [Fact]
    public async Task CanAsync_Should_EnqueueAuditEntry_When_Called()
    {
        var user = DefaultUser.ForClient(ClientKind.Wpf);
        await _sut.CanAsync(user, Permission.Pipeline_Trigger, "test-pipeline");

        // Read from the channel
        Assert.True(_auditWriter.Reader.TryRead(out var entry));
        Assert.Equal("Pipeline_Trigger", entry!.ActionName);
        Assert.Equal("test-pipeline", entry.ResourceId);
        Assert.True(entry.Allowed);
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
