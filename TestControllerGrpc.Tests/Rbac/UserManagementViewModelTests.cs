using System.Net.Http;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels.Admin;
using static TestControllerGrpc.Services.UserManagementClient;

namespace TestControllerGrpc.Tests.Rbac;

public class UserManagementViewModelTests
{
    private readonly FakeUserManagementClient _client = new();
    private readonly FakeAppLogger _logger = new();

    private UserManagementViewModel CreateSut() => new(_client, _logger);

    [Fact]
    public async Task LoadUsersCommand_Should_PopulateUsers_When_ServiceReturnsData()
    {
        _client.UsersToReturn =
        [
            new("u1", "alice", "a@test.com", "Engineer", true, false, DateTime.UtcNow, null, 2),
            new("u2", "bob", "b@test.com", "SeniorManager", true, false, DateTime.UtcNow, null, 0),
        ];

        var sut = CreateSut();
        await sut.LoadUsersCommand.ExecuteAsync(null);

        Assert.Equal(2, sut.Users.Count);
        Assert.Equal("alice", sut.Users[0].Username);
        Assert.Equal("bob", sut.Users[1].Username);
    }

    [Fact]
    public async Task LoadUsersCommand_Should_SetErrorMessage_When_ExceptionThrown()
    {
        _client.ThrowOnList = true;
        var sut = CreateSut();

        await sut.LoadUsersCommand.ExecuteAsync(null);

        Assert.Equal("Failed to load users", sut.ErrorMessage);
    }

    [Fact]
    public async Task OpenDeleteUserCommand_Should_RaiseEvent_When_UserSelected()
    {
        _client.UsersToReturn =
        [
            new("u1", "alice", "a@test.com", "Engineer", true, false, DateTime.UtcNow, null, 0),
        ];

        var sut = CreateSut();
        await sut.LoadUsersCommand.ExecuteAsync(null);
        sut.SelectedUser = sut.Users[0];

        var raised = false;
        sut.RequestOpenDeleteUser += () => raised = true;
        sut.OpenDeleteUserCommand.Execute(null);

        Assert.True(raised);
    }

    [Fact]
    public async Task OpenDeleteUserCommand_Should_NoOp_When_NoSelection()
    {
        var sut = CreateSut();
        sut.SelectedUser = null;

        var raised = false;
        sut.RequestOpenDeleteUser += () => raised = true;
        sut.OpenDeleteUserCommand.Execute(null);

        Assert.False(raised);
        Assert.Equal("", sut.ErrorMessage);
    }

    [Fact]
    public async Task OpenResetPasswordCommand_Should_RaiseEvent_When_UserSelected()
    {
        _client.UsersToReturn =
        [
            new("u1", "alice", "a@test.com", "Engineer", true, false, DateTime.UtcNow, null, 0),
        ];

        var sut = CreateSut();
        await sut.LoadUsersCommand.ExecuteAsync(null);
        sut.SelectedUser = sut.Users[0];

        var raised = false;
        sut.RequestOpenResetPassword += () => raised = true;
        sut.OpenResetPasswordCommand.Execute(null);

        Assert.True(raised);
    }

    [Fact]
    public async Task OpenResetPasswordCommand_Should_NoOp_When_NoSelection()
    {
        _client.UsersToReturn =
        [
            new("u1", "alice", "a@test.com", "Engineer", true, false, DateTime.UtcNow, null, 0),
        ];

        var sut = CreateSut();
        await sut.LoadUsersCommand.ExecuteAsync(null);
        sut.SelectedUser = null;

        var raised = false;
        sut.RequestOpenResetPassword += () => raised = true;
        sut.OpenResetPasswordCommand.Execute(null);

        Assert.False(raised);
    }

    [Fact]
    public void OpenAddUserCommand_Should_RaiseEvent()
    {
        var sut = CreateSut();
        var raised = false;
        sut.RequestOpenAddUser += () => raised = true;
        sut.OpenAddUserCommand.Execute(null);

        Assert.True(raised);
    }

    // ── Fakes ────────────────────────────────────────────────────────────

    private sealed class FakeUserManagementClient : UserManagementClient
    {
        public List<UserDto> UsersToReturn { get; set; } = [];
        public bool ThrowOnList { get; set; }
        public (bool Success, string? Error) DeleteResult { get; set; } = (true, null);
        public (string? NewPassword, string? Error) ResetResult { get; set; } = ("temppass", null);

        public FakeUserManagementClient() : base() { }

        public override async Task<List<UserDto>> ListUsersAsync(string? usernameFilter = null)
        {
            if (ThrowOnList) throw new HttpRequestException("Connection refused");
            await Task.CompletedTask;
            return UsersToReturn;
        }

        public override Task<(bool Success, string? Error)> DeleteUserAsync(string userId)
            => Task.FromResult(DeleteResult);

        public override Task<(string? NewPassword, string? Error)> ResetPasswordAsync(string userId)
            => Task.FromResult(ResetResult);
    }

    private sealed class FakeAppLogger : IAppLogger
    {
        public void Log(LogLevel level, string category, string message, Exception? ex = null) { }
        public void Log(LogLevel level, string category, string message, string? correlationId, long elapsedMs = 0, Exception? ex = null) { }
        public void Info(string category, string message) { }
        public void Warn(string category, string message) { }
        public void Error(string category, string message, Exception? ex = null) { }
        public IReadOnlyList<AppLogEntry> GetRecentEntries(int count = 500) => [];
        public event Action<AppLogEntry>? EntryAdded;
    }
}
