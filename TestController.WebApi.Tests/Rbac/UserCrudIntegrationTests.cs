using Microsoft.EntityFrameworkCore;
using TestController.Api.Services;
using TestController.Persistence;
using TestController.Persistence.Audit;
using TestController.Persistence.Identity;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Identity;

namespace TestController.WebApi.Tests.Rbac;

/// <summary>
/// Integration tests for user CRUD operations via UserService.
/// Per 02_Implementation_Roadmap.md §Phase 1 exit criteria.
/// </summary>
public class UserCrudIntegrationTests : IDisposable
{
    private readonly OrchestratorDbContext _db;
    private readonly UserService _userService;
    private readonly PasswordHasher _passwordHasher;
    private readonly string _adminUserId;

    public UserCrudIntegrationTests()
    {
        var dbOptions = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        _db = new OrchestratorDbContext(dbOptions);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        _passwordHasher = new PasswordHasher();
        var dbFactory = new TestDbContextFactory(_db);
        var sessionStore = new SessionStore(dbFactory);
        var auditWriter = new QueuedAuditWriter();

        _userService = new UserService(dbFactory, _passwordHasher, sessionStore, auditWriter);

        // Seed admin
        _adminUserId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        _db.Users.Add(new User
        {
            UserId = _adminUserId,
            Username = "admin",
            Email = "admin@test.com",
            Role = Role.Administrator,
            PasswordHash = _passwordHasher.Hash("Admin123!Pass"),
            IsActive = true,
            CreatedUtc = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CreateUser_Should_ReturnUser_When_ValidRequest()
    {
        var request = new UserService.CreateUserRequest("ravi.kumar", "ravi@test.com", "Engineer", null);
        var (user, generatedPassword, error) = await _userService.CreateUserAsync(request, _adminUserId);

        Assert.NotNull(user);
        Assert.NotNull(generatedPassword);
        Assert.Null(error);
        Assert.Equal("ravi.kumar", user!.Username);
        Assert.Equal("Engineer", user.Role);
        Assert.True(user.MustChangePassword);
        Assert.True(generatedPassword!.Length >= 12);
    }

    [Fact]
    public async Task CreateUser_Should_RejectAdministratorRole()
    {
        var request = new UserService.CreateUserRequest("newadmin", "new@test.com", "Administrator", null);
        var (user, _, error) = await _userService.CreateUserAsync(request, _adminUserId);

        Assert.Null(user);
        Assert.Equal("Cannot create Administrator users via this endpoint", error);
    }

    [Fact]
    public async Task CreateUser_Should_RejectDuplicateUsername()
    {
        var request1 = new UserService.CreateUserRequest("duplicate", "d1@test.com", "Engineer", null);
        await _userService.CreateUserAsync(request1, _adminUserId);

        var request2 = new UserService.CreateUserRequest("duplicate", "d2@test.com", "SeniorManager", null);
        var (user, _, error) = await _userService.CreateUserAsync(request2, _adminUserId);

        Assert.Null(user);
        Assert.Equal("Username already exists", error);
    }

    [Fact]
    public async Task ListUsers_Should_ReturnAllUsers()
    {
        await _userService.CreateUserAsync(
            new UserService.CreateUserRequest("user1", "u1@test.com", "Engineer", null), _adminUserId);
        await _userService.CreateUserAsync(
            new UserService.CreateUserRequest("user2", "u2@test.com", "SeniorManager", null), _adminUserId);

        var users = await _userService.ListUsersAsync();
        Assert.Equal(3, users.Count); // admin + user1 + user2
    }

    [Fact]
    public async Task DeleteUser_Should_CascadeAssignments()
    {
        // Create user with assignments
        var (createdUser, _, _) = await _userService.CreateUserAsync(
            new UserService.CreateUserRequest("todelete", "td@test.com", "Engineer", null), _adminUserId);
        var userId = createdUser!.UserId;

        await _userService.SetAssignmentsAsync(userId, ["pipeline-1", "pipeline-2"], _adminUserId);

        // Verify assignments exist
        var assignmentsBefore = await _userService.GetAssignmentsAsync(userId);
        Assert.Equal(2, assignmentsBefore.Count);

        // Delete user
        var (success, error) = await _userService.DeleteUserAsync(userId, _adminUserId);
        Assert.True(success);
        Assert.Null(error);

        // Verify user is gone
        var user = await _userService.GetUserAsync(userId);
        Assert.Null(user);

        // Verify assignments are gone
        var assignmentsAfter = await _userService.GetAssignmentsAsync(userId);
        Assert.Empty(assignmentsAfter);
    }

    [Fact]
    public async Task DeleteUser_Should_FailForLastAdministrator()
    {
        var (success, error) = await _userService.DeleteUserAsync(_adminUserId, _adminUserId);

        Assert.False(success);
        Assert.Equal("Cannot delete the last Administrator", error);
    }

    [Fact]
    public async Task SetAssignments_Should_ComputeDiff()
    {
        var (user, _, _) = await _userService.CreateUserAsync(
            new UserService.CreateUserRequest("engineer1", "e1@test.com", "Engineer", null), _adminUserId);
        var userId = user!.UserId;

        // Initial assignment: pipeline-1, pipeline-2
        await _userService.SetAssignmentsAsync(userId, ["pipeline-1", "pipeline-2"], _adminUserId);
        var initial = await _userService.GetAssignmentsAsync(userId);
        Assert.Equal(2, initial.Count);

        // Update: keep pipeline-1, remove pipeline-2, add pipeline-3
        await _userService.SetAssignmentsAsync(userId, ["pipeline-1", "pipeline-3"], _adminUserId);
        var updated = await _userService.GetAssignmentsAsync(userId);
        Assert.Contains("pipeline-1", updated);
        Assert.Contains("pipeline-3", updated);
        Assert.DoesNotContain("pipeline-2", updated);
    }

    [Fact]
    public async Task ResetPassword_Should_SetMustChangePassword()
    {
        var (user, _, _) = await _userService.CreateUserAsync(
            new UserService.CreateUserRequest("resetme", "r@test.com", "Engineer", "InitialPass1!"), _adminUserId);
        var userId = user!.UserId;

        var (newPassword, error) = await _userService.ResetPasswordAsync(userId, _adminUserId);

        Assert.NotNull(newPassword);
        Assert.Null(error);
        Assert.True(newPassword!.Length >= 12);

        // Verify MustChangePassword is set
        var updated = await _userService.GetUserAsync(userId);
        Assert.True(updated!.MustChangePassword);
    }

    [Fact]
    public async Task UpdateUser_Should_RejectAdministratorRole()
    {
        var (user, _, _) = await _userService.CreateUserAsync(
            new UserService.CreateUserRequest("upgradetest", "u@test.com", "Engineer", null), _adminUserId);

        var (success, error) = await _userService.UpdateUserAsync(
            user!.UserId, new UserService.UpdateUserRequest(null, "Administrator"), _adminUserId);

        Assert.False(success);
        Assert.Equal("Cannot assign Administrator role via this endpoint", error);
    }

    [Fact]
    public async Task UsernameExists_Should_ReturnTrue_When_Exists()
    {
        await _userService.CreateUserAsync(
            new UserService.CreateUserRequest("checkme", "c@test.com", "Engineer", null), _adminUserId);

        Assert.True(await _userService.UsernameExistsAsync("checkme"));
        Assert.False(await _userService.UsernameExistsAsync("nonexistent"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────

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
