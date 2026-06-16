using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TestController.Api.Controllers;
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
/// Integration tests for AuditController (GET /api/audit, GET /api/audit/export).
/// Per Phase 10: audit viewer, admin-only.
/// </summary>
public class AuditControllerTests : IDisposable
{
    private readonly OrchestratorDbContext _db;
    private readonly SessionAuthInterceptor _interceptor;
    private readonly AuthService _authService;
    private readonly PasswordHasher _passwordHasher;
    private readonly IDbContextFactory<OrchestratorDbContext> _dbFactory;
    private readonly AuditController _sut;
    private string _adminToken = "";
    private string _operatorToken = "";

    public AuditControllerTests()
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
        _dbFactory = new TestDbContextFactory(_db);
        var sessionStore = new SessionStore(_dbFactory);
        var auditWriter = new QueuedAuditWriter();
        _interceptor = new SessionAuthInterceptor(optionsMonitor, sessionStore, _dbFactory);
        _authService = new AuthService(_dbFactory, sessionStore, _passwordHasher, auditWriter);

        _sut = new AuditController(_dbFactory, _interceptor);

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
            UserId = "op-1",
            Username = "operator",
            Email = "op@test.com",
            Role = Role.Engineer,
            PasswordHash = _passwordHasher.Hash("Op123!Pass"),
            IsActive = true,
            CreatedUtc = DateTime.UtcNow,
        });

        // Seed audit entries
        for (int i = 0; i < 50; i++)
        {
            _db.AuditEntries.Add(new AuditEntry
            {
                UserId = i % 2 == 0 ? "admin-1" : "op-1",
                ActionName = i % 3 == 0 ? "Pipeline_Trigger" : "Pipeline_View",
                ResourceId = $"pipeline-{i % 5}",
                Allowed = i % 4 != 0,
                ReasonCode = i % 4 != 0 ? "role-granted" : "denied-insufficient-role",
                TimestampUtc = DateTime.UtcNow.AddHours(-i),
                ClientKind = ClientKind.Web,
                CorrelationId = Guid.NewGuid().ToString(),
            });
        }
        _db.SaveChanges();
    }

    private async Task EnsureTokens()
    {
        if (!string.IsNullOrEmpty(_adminToken)) return;
        var adminResult = await _authService.LoginAsync("admin", "Admin123!", ClientKind.Web, "127.0.0.1");
        _adminToken = adminResult.Token!;
        var opResult = await _authService.LoginAsync("operator", "Op123!Pass", ClientKind.Web, "127.0.0.1");
        _operatorToken = opResult.Token!;
    }

    private void SetAuthHeader(string token)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = $"Bearer {token}";
        _sut.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    public void Dispose() => _db.Dispose();

    // ── Authorization Tests ─────────────────────────────────────────────

    [Fact]
    public async Task Query_Should_Return403_When_NonAdmin()
    {
        await EnsureTokens();
        SetAuthHeader(_operatorToken);

        var result = await _sut.Query(new AuditQueryParams(), CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task Query_Should_Return403_When_NoAuthHeader()
    {
        var httpContext = new DefaultHttpContext();
        _sut.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var result = await _sut.Query(new AuditQueryParams(), CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    // ── Query Tests ─────────────────────────────────────────────────────

    [Fact]
    public async Task Query_Should_ReturnPaginatedResults_When_Admin()
    {
        await EnsureTokens();
        SetAuthHeader(_adminToken);

        var result = await _sut.Query(new AuditQueryParams { PageSize = 10 }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var page = Assert.IsType<AuditPageResponse>(ok.Value);
        Assert.Equal(50, page.Total);
        Assert.Equal(10, page.Items.Count);
        Assert.Equal(1, page.Page);
    }

    [Fact]
    public async Task Query_Should_FilterByActionName_When_ExactMatch()
    {
        await EnsureTokens();
        SetAuthHeader(_adminToken);

        var result = await _sut.Query(new AuditQueryParams { ActionName = "Pipeline_Trigger" }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var page = Assert.IsType<AuditPageResponse>(ok.Value);
        // Every 3rd entry (0,3,6,...49) → indices 0,3,6,9,...,48 → 17 entries
        Assert.Equal(17, page.Total);
        Assert.All(page.Items, item => Assert.Equal("Pipeline_Trigger", item.ActionName));
    }

    [Fact]
    public async Task Query_Should_FilterByActionWildcard_When_StarUsed()
    {
        await EnsureTokens();
        SetAuthHeader(_adminToken);

        var result = await _sut.Query(new AuditQueryParams { ActionName = "Pipeline_*" }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var page = Assert.IsType<AuditPageResponse>(ok.Value);
        Assert.Equal(50, page.Total); // All entries are Pipeline_Trigger or Pipeline_View
    }

    [Fact]
    public async Task Query_Should_FilterByDecision_When_DeniedOnly()
    {
        await EnsureTokens();
        SetAuthHeader(_adminToken);

        var result = await _sut.Query(new AuditQueryParams { Decision = false }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var page = Assert.IsType<AuditPageResponse>(ok.Value);
        // Every 4th (i%4==0): indices 0,4,8,...48 → 13 entries
        Assert.Equal(13, page.Total);
        Assert.All(page.Items, item => Assert.False(item.Allowed));
    }

    [Fact]
    public async Task Query_Should_FilterByUserId_When_Specified()
    {
        await EnsureTokens();
        SetAuthHeader(_adminToken);

        var result = await _sut.Query(new AuditQueryParams { UserId = "admin-1" }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var page = Assert.IsType<AuditPageResponse>(ok.Value);
        // Even indices: 0,2,4,...48 → 25 entries
        Assert.Equal(25, page.Total);
    }

    [Fact]
    public async Task Query_Should_OrderByTimestampDesc_When_Default()
    {
        await EnsureTokens();
        SetAuthHeader(_adminToken);

        var result = await _sut.Query(new AuditQueryParams { PageSize = 5 }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var page = Assert.IsType<AuditPageResponse>(ok.Value);
        for (int i = 0; i < page.Items.Count - 1; i++)
        {
            Assert.True(page.Items[i].TimestampUtc >= page.Items[i + 1].TimestampUtc);
        }
    }

    [Fact]
    public async Task Query_Should_RespectPagination_When_Page2()
    {
        await EnsureTokens();
        SetAuthHeader(_adminToken);

        var result = await _sut.Query(new AuditQueryParams { Page = 2, PageSize = 20 }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var page = Assert.IsType<AuditPageResponse>(ok.Value);
        Assert.Equal(2, page.Page);
        Assert.Equal(20, page.Items.Count);
        Assert.Equal(50, page.Total);
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
            _options = new DbContextOptionsBuilder<OrchestratorDbContext>()
                .UseSqlite(conn)
                .Options;
        }

        public OrchestratorDbContext CreateDbContext() => new(_options);
        public Task<OrchestratorDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
