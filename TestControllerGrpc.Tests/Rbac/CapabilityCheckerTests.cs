using Microsoft.Extensions.Options;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Configuration;
using TestControllerGrpc.Identity;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Rbac;

public class CapabilityCheckerTests
{
    private readonly RbacOptions _options;
    private readonly CurrentUserHolder _userHolder;
    private readonly CapabilityChecker _checker;

    public CapabilityCheckerTests()
    {
        _options = new RbacOptions { Enabled = true };
        var optionsMonitor = new TestOptionsMonitor<RbacOptions>(_options);
        _userHolder = new CurrentUserHolder(optionsMonitor);
        _checker = new CapabilityChecker(_userHolder, optionsMonitor);
    }

    [Fact]
    public void Can_Should_ReturnTrue_When_DefaultMode()
    {
        _options.Enabled = false;
        Assert.True(_checker.Can(Permission.Pipeline_Trigger, "any-pipeline"));
    }

    [Fact]
    public void Can_Should_ReturnTrue_When_AdminUser()
    {
        _userHolder.SetUser(MakeAuthUser(Role.Administrator));
        Assert.True(_checker.Can(Permission.Pipeline_Trigger, "any-pipeline"));
        Assert.True(_checker.Can(Permission.User_Create));
    }

    [Fact]
    public void Can_Should_ReturnTrue_When_EngineerAssignedToPipeline()
    {
        _userHolder.SetUser(MakeAuthUser(Role.Engineer, ["pipeline-A", "pipeline-B"]));
        Assert.True(_checker.Can(Permission.Pipeline_Trigger, "pipeline-A"));
    }

    [Fact]
    public void Can_Should_ReturnFalse_When_EngineerNotAssignedToPipeline()
    {
        _userHolder.SetUser(MakeAuthUser(Role.Engineer, ["pipeline-A"]));
        Assert.False(_checker.Can(Permission.Pipeline_Trigger, "pipeline-C"));
    }

    [Fact]
    public void Can_Should_ReturnTrue_When_SeniorManagerAnyPipeline()
    {
        _userHolder.SetUser(MakeAuthUser(Role.SeniorManager));
        Assert.True(_checker.Can(Permission.Pipeline_Trigger, "any-pipeline"));
        Assert.True(_checker.Can(Permission.Pipeline_Cancel, "any-pipeline"));
    }

    [Fact]
    public void Can_Should_ReturnFalse_When_EngineerLacksPermission()
    {
        _userHolder.SetUser(MakeAuthUser(Role.Engineer, ["pipeline-A"]));
        Assert.False(_checker.Can(Permission.User_Create));
    }

    [Fact]
    public void Can_Should_ReturnFalse_When_GuestTriesWrite()
    {
        _userHolder.SetUser(MakeAuthUser(Role.Guest));
        Assert.False(_checker.Can(Permission.Pipeline_Trigger, "any-pipeline"));
    }

    [Fact]
    public void CapabilitiesChanged_Should_Fire_When_UserChanges()
    {
        var fired = false;
        _checker.CapabilitiesChanged += () => fired = true;

        _userHolder.SetUser(MakeAuthUser(Role.Engineer, ["pipeline-A"]));

        Assert.True(fired);
    }

    [Fact]
    public void CapabilitiesChanged_Should_Fire_When_UserCleared()
    {
        _userHolder.SetUser(MakeAuthUser(Role.Engineer, ["pipeline-A"]));

        var fired = false;
        _checker.CapabilitiesChanged += () => fired = true;
        _userHolder.Clear();

        Assert.True(fired);
    }

    private static AuthUserInfo MakeAuthUser(Role role, List<string>? assignedPipelineIds = null)
    {
        return new AuthUserInfo(
            UserId: Guid.NewGuid().ToString("D"),
            Username: "testuser",
            DisplayName: "Test User",
            Role: role.ToString(),
            ClientKind: ClientKind.Wpf.ToString(),
            Capabilities: PermissionCatalog.GetPermissionsForRole(role.ToString())
                .Select(p => p.ToString()).ToList(),
            MustChangePassword: false,
            IsGuest: role == Role.Guest,
            AssignedPipelineIds: assignedPipelineIds ?? []);
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private readonly T _value;
        public TestOptionsMonitor(T value) => _value = value;
        public T CurrentValue => _value;
        public T Get(string? name) => _value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
