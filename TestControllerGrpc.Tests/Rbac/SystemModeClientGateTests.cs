using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TestController.Api.Controllers;
using TestController.Api.Hubs;
using TestController.Api.Interceptors;
using TestController.Api.SystemMode;
using TestController.Persistence;
using TestController.Persistence.Audit;
using TestController.Persistence.Authorization;
using TestController.Persistence.Identity;
using TestControllerGrpc.Configuration;
using TestControllerGrpc.Identity;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Rbac;

/// <summary>
/// Drives the real WPF <see cref="SystemModeClient"/> through the real SystemModeController gate
/// (SessionAuthInterceptor + AuthorizationService + RbacModeTransitionService). The VM tests fake
/// the client, so nothing else proves the client sends the identity the P4-1 gate demands.
/// </summary>
public sealed class SystemModeClientGateTests : IDisposable
{
    private const string GateDeniedMessage = "Not authorized to change system mode.";

    private readonly OrchestratorDbContext _db;
    private readonly RbacOptions _options = new() { Enabled = true };
    private readonly SessionStore _sessionStore;
    private readonly SystemModeController _controller;
    private readonly FakeAppLogger _logger = new();

    public SystemModeClientGateTests()
    {
        _db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        var dbFactory = new TestDbContextFactory(_db);
        var optionsMonitor = new TestOptionsMonitor<RbacOptions>(_options);
        var auditWriter = new QueuedAuditWriter();
        _sessionStore = new SessionStore(dbFactory);

        _controller = new SystemModeController(
            optionsMonitor,
            new RbacModeTransitionService(
                dbFactory, new TestWritableOptions(_options), _sessionStore, new PasswordHasher(), auditWriter),
            new SystemModeBroadcaster(),
            new SessionAuthInterceptor(optionsMonitor, _sessionStore, dbFactory),
            new AuthorizationService(optionsMonitor, dbFactory, auditWriter));
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task SwitchToDefaultAsync_Should_Succeed_When_SecuredAndAdministratorSignedIn()
    {
        var admin = await SeedUserAsync(Role.Administrator);
        var client = CreateClient(await SignedInAsync(admin));

        var (success, error) = await client.SwitchToDefaultAsync();

        Assert.True(success, error);
        Assert.Null(error);
        Assert.False(_options.Enabled);
    }

    [Fact]
    public async Task SwitchToDefaultAsync_Should_BeDenied_When_SecuredAndNobodySignedIn()
    {
        await SeedUserAsync(Role.Administrator);
        var client = CreateClient(SignedOut());

        var (success, error) = await client.SwitchToDefaultAsync();

        Assert.False(success);
        Assert.Equal(GateDeniedMessage, error);
        Assert.True(_options.Enabled);
    }

    [Fact]
    public async Task SwitchToDefaultAsync_Should_BeDenied_When_SecuredAndEngineerSignedIn()
    {
        var engineer = await SeedUserAsync(Role.Engineer);
        var client = CreateClient(await SignedInAsync(engineer));

        var (success, error) = await client.SwitchToDefaultAsync();

        Assert.False(success);
        Assert.Equal(GateDeniedMessage, error);
        Assert.True(_options.Enabled);
    }

    [Fact]
    public async Task SwitchToSecuredReactivateAsync_Should_Succeed_When_DefaultModeAndAdministratorArchived()
    {
        _options.Enabled = false;
        await SeedUserAsync(Role.Administrator, isActive: false);
        var client = CreateClient(SignedOut());

        var (success, error) = await client.SwitchToSecuredReactivateAsync();

        Assert.True(success, error);
        Assert.Null(error);
        Assert.True(_options.Enabled);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private SystemModeClient CreateClient(AuthClient authClient) =>
        new(new FixedClientFactory(new ControllerBridgeHandler(_controller)),
            authClient,
            new TestOptionsMonitor<RbacOptions>(_options),
            _logger);

    private async Task<User> SeedUserAsync(Role role, bool isActive = true)
    {
        var user = new User
        {
            UserId = Guid.NewGuid().ToString("D").ToLowerInvariant(),
            Username = $"{role}-{Guid.NewGuid():N}",
            Email = "user@corp.com",
            Role = role,
            PasswordHash = "hash",
            IsActive = isActive,
            CreatedUtc = DateTime.UtcNow,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    /// <summary>A real AuthClient that logged in and now holds a server-valid session token.</summary>
    private async Task<AuthClient> SignedInAsync(User user)
    {
        var session = await _sessionStore.CreateSessionAsync(user.UserId, null, ClientKind.Wpf, null);
        var token = SessionStore.TokenToString(session.TokenHash);

        var authClient = new AuthClient(new FixedClientFactory(new StubHandler(request =>
            request.RequestUri!.AbsolutePath == "/api/auth/login"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { token, role = user.Role.ToString(), mustChangePassword = false }),
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound))), _logger);

        var login = await authClient.LoginAsync(user.Username, "irrelevant");
        Assert.True(login.Success);
        return authClient;
    }

    private AuthClient SignedOut() =>
        new(new FixedClientFactory(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))), _logger);

    private sealed class FixedClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost:5200") };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    /// <summary>
    /// Stands in for Kestrel: hands the client's exact request headers to the real controller action
    /// and turns its IActionResult back into an HTTP response.
    /// </summary>
    private sealed class ControllerBridgeHandler(SystemModeController controller) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var http = new DefaultHttpContext();
            foreach (var header in request.Headers)
                http.Request.Headers[header.Key] = header.Value.ToArray();
            controller.ControllerContext = new ControllerContext { HttpContext = http };

            IActionResult result = request.RequestUri!.AbsolutePath switch
            {
                "/api/system/mode/default" => await controller.SwitchToDefault(ct),
                "/api/system/mode/secured/reactivate" => await controller.SwitchToSecuredReactivate(ct),
                var other => throw new InvalidOperationException($"No bridge route for {other}"),
            };

            var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
            return new HttpResponseMessage((HttpStatusCode)(objectResult.StatusCode ?? 200))
            {
                Content = JsonContent.Create(objectResult.Value, objectResult.Value?.GetType() ?? typeof(object)),
            };
        }
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class TestWritableOptions(RbacOptions value) : IWritableOptions<RbacOptions>
    {
        public RbacOptions Value => value;
        public void Update(Action<RbacOptions> applyChanges) => applyChanges(value);
    }

    private sealed class TestDbContextFactory : IDbContextFactory<OrchestratorDbContext>
    {
        private readonly DbContextOptions<OrchestratorDbContext> _options;

        public TestDbContextFactory(OrchestratorDbContext seedDb)
        {
            // Share the same in-memory connection
            var connection = (Microsoft.Data.Sqlite.SqliteConnection)seedDb.Database.GetDbConnection();
            _options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseSqlite(connection).Options;
        }

        public OrchestratorDbContext CreateDbContext() => new(_options);
        public Task<OrchestratorDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class FakeAppLogger : IAppLogger
    {
        public void Info(string category, string message) { }
        public void Warn(string category, string message) { }
        public void Error(string category, string message, Exception? ex = null) { }
        public void Log(Microsoft.Extensions.Logging.LogLevel level, string category, string message, Exception? ex = null) { }
        public void Log(Microsoft.Extensions.Logging.LogLevel level, string category, string message, string? correlationId, long elapsedMs = 0, Exception? ex = null) { }
        public void LogStructured(Microsoft.Extensions.Logging.LogLevel level, string category, string message, string? agent = null, string? runId = null, string? pipeline = null, string? action = null, long elapsedMs = 0, Exception? ex = null) { }
        public IReadOnlyList<AppLogEntry> GetRecentEntries(int count = 500) => [];
        public event Action<AppLogEntry>? EntryAdded;
    }
}
