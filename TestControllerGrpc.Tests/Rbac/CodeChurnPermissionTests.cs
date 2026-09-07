using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TestController.Persistence;
using TestController.Persistence.Audit;
using TestController.Persistence.Authorization;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Configuration;
using TestControllerGrpc.Identity;

namespace TestControllerGrpc.Tests.Rbac;

/// <summary>
/// Workstream D: Code Churn and Report Card are restricted to Administrator and Senior Manager.
/// The classification is easy to get subtly wrong — putting these in the read bucket would also hand them
/// to Guests — so every role is asserted explicitly rather than by sampling.
/// </summary>
public class CodeChurnPermissionTests : IDisposable
{
    private static readonly Permission[] RestrictedPermissions =
        [Permission.CodeChurn_View, Permission.CodeChurn_Export, Permission.ReportCard_View];

    private readonly OrchestratorDbContext _db;
    private readonly AuthorizationService _sut;
    private readonly RbacOptions _options;

    public CodeChurnPermissionTests()
    {
        var dbOptions = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        _db = new OrchestratorDbContext(dbOptions);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        _options = new RbacOptions { Enabled = true };
        _sut = new AuthorizationService(
            new TestOptionsMonitor<RbacOptions>(_options), new TestDbContextFactory(_db), new QueuedAuditWriter());
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private static SyntheticUserContext User(Role role, ClientKind kind = ClientKind.Web) =>
        new($"{role}-id", role.ToString(), kind, [role.ToString()],
            guestId: role == Role.Guest ? "guest-id" : null);

    public static TheoryData<Permission> Restricted()
    {
        var data = new TheoryData<Permission>();
        foreach (Permission p in RestrictedPermissions) data.Add(p);
        return data;
    }

    [Theory]
    [MemberData(nameof(Restricted))]
    public async Task CanAsync_Should_Allow_When_Administrator(Permission permission)
    {
        Assert.True((await _sut.CanAsync(User(Role.Administrator), permission)).Allowed);
    }

    [Theory]
    [MemberData(nameof(Restricted))]
    public async Task CanAsync_Should_Allow_When_SeniorManager(Permission permission)
    {
        Assert.True((await _sut.CanAsync(User(Role.SeniorManager), permission)).Allowed);
    }

    [Theory]
    [MemberData(nameof(Restricted))]
    public async Task CanAsync_Should_Deny_When_Engineer(Permission permission)
    {
        Assert.False((await _sut.CanAsync(User(Role.Engineer), permission)).Allowed);
    }

    [Theory]
    [MemberData(nameof(Restricted))]
    public async Task CanAsync_Should_Deny_When_Guest(Permission permission)
    {
        // Would silently pass if these were classified as read permissions.
        Assert.False((await _sut.CanAsync(User(Role.Guest), permission)).Allowed);
    }

    [Theory]
    [MemberData(nameof(Restricted))]
    public async Task CanAsync_Should_Allow_When_DefaultModeAndWpf(Permission permission)
    {
        // Default mode must behave exactly as before this workstream.
        _options.Enabled = false;

        Assert.True((await _sut.CanAsync(DefaultUser.ForClient(ClientKind.Wpf), permission)).Allowed);
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
    }
}
