using Microsoft.Extensions.Options;
using TestControllerGrpc.Configuration;
using TestControllerGrpc.ViewModels.Settings;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Rbac;

public class SecurityModeViewModelTests
{
    private readonly RbacOptions _options;
    private readonly TestOptionsMonitor<RbacOptions> _optionsMonitor;
    private readonly FakeSystemModeClient _fakeClient;
    private readonly FakeAppLogger _fakeLogger;
    private readonly SecurityModeViewModel _sut;

    public SecurityModeViewModelTests()
    {
        _options = new RbacOptions { Enabled = false };
        _optionsMonitor = new TestOptionsMonitor<RbacOptions>(_options);
        _fakeClient = new FakeSystemModeClient();
        _fakeLogger = new FakeAppLogger();
        _sut = new SecurityModeViewModel(_optionsMonitor, _fakeClient, _fakeLogger);
    }

    [Fact]
    public void IsDefaultMode_Should_BeTrue_When_RbacDisabled()
    {
        Assert.True(_sut.IsDefaultMode);
        Assert.False(_sut.IsSecuredMode);
    }

    [Fact]
    public void IsSecuredMode_Should_BeTrue_When_RbacEnabled()
    {
        _options.Enabled = true;
        var vm = new SecurityModeViewModel(_optionsMonitor, _fakeClient, _fakeLogger);

        Assert.True(vm.IsSecuredMode);
        Assert.False(vm.IsDefaultMode);
    }

    [Fact]
    public void OpenWizardCommand_Should_SetIsWizardOpenTrue()
    {
        _sut.OpenWizardCommand.Execute(null);

        Assert.True(_sut.IsWizardOpen);
    }

    [Fact]
    public void CloseWizardCommand_Should_SetIsWizardOpenFalse()
    {
        _sut.IsWizardOpen = true;
        _sut.CloseWizardCommand.Execute(null);

        Assert.False(_sut.IsWizardOpen);
    }

    [Fact]
    public void IsWizardValid_Should_BeFalse_When_PasswordTooShort()
    {
        _sut.WizardUsername = "admin";
        _sut.WizardEmail = "admin@test.com";
        _sut.WizardPassword = "short";
        _sut.WizardConfirmPassword = "short";

        Assert.False(_sut.IsWizardValid);
    }

    [Fact]
    public void IsWizardValid_Should_BeFalse_When_PasswordsMismatch()
    {
        _sut.WizardUsername = "admin";
        _sut.WizardEmail = "admin@test.com";
        _sut.WizardPassword = "LongPassword123!";
        _sut.WizardConfirmPassword = "DifferentPass123!";

        Assert.False(_sut.IsWizardValid);
    }

    [Fact]
    public void IsWizardValid_Should_BeTrue_When_AllFieldsValid()
    {
        _sut.WizardUsername = "admin";
        _sut.WizardEmail = "admin@test.com";
        _sut.WizardPassword = "LongPassword123!";
        _sut.WizardConfirmPassword = "LongPassword123!";

        Assert.True(_sut.IsWizardValid);
    }

    [Fact]
    public void CanConfirmDisable_Should_BeFalse_When_TextDoesNotMatch()
    {
        _sut.DisableConfirmText = "disable rbac"; // case-sensitive mismatch

        Assert.False(_sut.CanConfirmDisable);
    }

    [Fact]
    public void CanConfirmDisable_Should_BeTrue_When_ExactMatch()
    {
        _sut.DisableConfirmText = "DISABLE RBAC";

        Assert.True(_sut.CanConfirmDisable);
    }

    [Fact]
    public async Task ConfirmSwitchToSecuredCommand_Should_CloseWizard_When_Successful()
    {
        _fakeClient.NextResult = (true, null);
        _sut.WizardUsername = "admin";
        _sut.WizardEmail = "admin@test.com";
        _sut.WizardPassword = "LongPassword123!";
        _sut.WizardConfirmPassword = "LongPassword123!";
        _sut.IsWizardOpen = true;

        await _sut.ConfirmSwitchToSecuredCommand.ExecuteAsync(null);

        Assert.False(_sut.IsWizardOpen);
        Assert.Equal("Switched to Secured mode.", _sut.StatusMessage);
    }

    [Fact]
    public async Task ConfirmSwitchToSecuredCommand_Should_ShowError_When_Failed()
    {
        _fakeClient.NextResult = (false, "Username already exists");
        _sut.WizardUsername = "admin";
        _sut.WizardEmail = "admin@test.com";
        _sut.WizardPassword = "LongPassword123!";
        _sut.WizardConfirmPassword = "LongPassword123!";
        _sut.IsWizardOpen = true;

        await _sut.ConfirmSwitchToSecuredCommand.ExecuteAsync(null);

        Assert.True(_sut.IsWizardOpen);
        Assert.Equal("Username already exists", _sut.WizardError);
    }

    [Fact]
    public async Task ConfirmSwitchToDefaultCommand_Should_CloseDialog_When_Successful()
    {
        _fakeClient.NextDefaultResult = (true, null);
        _sut.DisableConfirmText = "DISABLE RBAC";
        _sut.IsDisableDialogOpen = true;

        await _sut.ConfirmSwitchToDefaultCommand.ExecuteAsync(null);

        Assert.False(_sut.IsDisableDialogOpen);
        Assert.Equal("Switched to Default mode.", _sut.StatusMessage);
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

    private sealed class FakeSystemModeClient : SystemModeClient
    {
        public (bool, string?) NextResult { get; set; } = (true, null);
        public (bool, string?) NextDefaultResult { get; set; } = (true, null);

        public FakeSystemModeClient() : base() { }

        public override Task<(bool Success, string? Error)> SwitchToSecuredAsync(
            string username, string email, string password)
            => Task.FromResult(NextResult);

        public override Task<(bool Success, string? Error)> SwitchToDefaultAsync()
            => Task.FromResult(NextDefaultResult);
    }

    private sealed class FakeAppLogger : IAppLogger
    {
        public void Info(string category, string message) { }
        public void Warn(string category, string message) { }
        public void Error(string category, string message, Exception? ex = null) { }
        public void Log(Microsoft.Extensions.Logging.LogLevel level, string category, string message, Exception? ex = null) { }
        public void Log(Microsoft.Extensions.Logging.LogLevel level, string category, string message, string? correlationId, long elapsedMs = 0, Exception? ex = null) { }
        public IReadOnlyList<AppLogEntry> GetRecentEntries(int count = 500) => [];
        public event Action<AppLogEntry>? EntryAdded;
    }
}
