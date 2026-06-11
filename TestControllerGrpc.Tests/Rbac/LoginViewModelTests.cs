using System.Net.Http;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels.Login;

namespace TestControllerGrpc.Tests.Rbac;

public class LoginViewModelTests
{
    private readonly FakeAuthClient _authClient = new();
    private readonly FakeAppLogger _logger = new();

    private LoginViewModel CreateSut() => new(_authClient, _logger);

    [Fact]
    public async Task SignInCommand_Should_RaiseLoginSucceeded_When_CredentialsValid()
    {
        _authClient.NextResult = new AuthClient.LoginResult(true, MustChangePassword: false);
        var sut = CreateSut();
        bool succeeded = false;
        bool mustChange = true;
        sut.LoginSucceeded += (mcp) => { succeeded = true; mustChange = mcp; };

        sut.Username = "admin";
        sut.Password = "P@ssword123!";
        await sut.SignInCommand.ExecuteAsync(null);

        Assert.True(succeeded);
        Assert.False(mustChange);
        Assert.Equal("", sut.ErrorMessage);
    }

    [Fact]
    public async Task SignInCommand_Should_SetMustChangePassword_When_FlagTrue()
    {
        _authClient.NextResult = new AuthClient.LoginResult(true, MustChangePassword: true);
        var sut = CreateSut();
        bool mustChange = false;
        sut.LoginSucceeded += (mcp) => { mustChange = mcp; };

        sut.Username = "newuser";
        sut.Password = "InitialPass1!";
        await sut.SignInCommand.ExecuteAsync(null);

        Assert.True(mustChange);
    }

    [Fact]
    public async Task SignInCommand_Should_ShowError_When_CredentialsInvalid()
    {
        _authClient.NextResult = new AuthClient.LoginResult(false, Error: "Invalid username or password");
        var sut = CreateSut();
        bool succeeded = false;
        sut.LoginSucceeded += (_) => { succeeded = true; };

        sut.Username = "admin";
        sut.Password = "wrongpassword";
        await sut.SignInCommand.ExecuteAsync(null);

        Assert.False(succeeded);
        Assert.Equal("Invalid username or password", sut.ErrorMessage);
    }

    [Fact]
    public void SignInCommand_Should_NotExecute_When_UsernameEmpty()
    {
        var sut = CreateSut();
        sut.Username = "";
        sut.Password = "somepass";

        Assert.False(sut.SignInCommand.CanExecute(null));
    }

    [Fact]
    public void SignInCommand_Should_NotExecute_When_PasswordEmpty()
    {
        var sut = CreateSut();
        sut.Username = "admin";
        sut.Password = "";

        Assert.False(sut.SignInCommand.CanExecute(null));
    }

    [Fact]
    public async Task SignInCommand_Should_ShowConnectionError_When_ExceptionThrown()
    {
        _authClient.ThrowOnLogin = true;
        var sut = CreateSut();

        sut.Username = "admin";
        sut.Password = "P@ssword123!";
        await sut.SignInCommand.ExecuteAsync(null);

        Assert.Equal("Connection error. Please try again.", sut.ErrorMessage);
    }

    [Fact]
    public async Task SignInCommand_Should_TrimUsername_When_HasWhitespace()
    {
        _authClient.NextResult = new AuthClient.LoginResult(true);
        var sut = CreateSut();
        sut.LoginSucceeded += (_) => { };

        sut.Username = "  admin  ";
        sut.Password = "P@ssword123!";
        await sut.SignInCommand.ExecuteAsync(null);

        Assert.Equal("admin", _authClient.LastUsername);
    }

    // ── Fakes ────────────────────────────────────────────────────────────

    private sealed class FakeAuthClient : AuthClient
    {
        public AuthClient.LoginResult NextResult { get; set; } = new(false, Error: "Not configured");
        public bool ThrowOnLogin { get; set; }
        public string? LastUsername { get; private set; }

        public FakeAuthClient() : base() { }

        public override async Task<LoginResult> LoginAsync(string username, string password)
        {
            LastUsername = username;
            if (ThrowOnLogin)
                throw new HttpRequestException("Connection refused");
            await Task.CompletedTask;
            return NextResult;
        }
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
