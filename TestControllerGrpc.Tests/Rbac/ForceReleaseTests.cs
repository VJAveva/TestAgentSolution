using Microsoft.Extensions.Options;
using Moq;
using TestController.Api.Services;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Identity;
using TestControllerGrpc.Locking;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Rbac;

public sealed class ForceReleaseTests
{
    private readonly LockRegistry _registry;
    private readonly Mock<IAuthorizationService> _authzMock;
    private readonly Mock<IAuditWriter> _auditMock;
    private readonly Mock<IAppLogger> _loggerMock;
    private readonly LockService _lockService;

    public ForceReleaseTests()
    {
        _registry = new LockRegistry(Options.Create(new LockOptions { HeartbeatTimeoutSeconds = 30 }));
        _authzMock = new Mock<IAuthorizationService>();
        _auditMock = new Mock<IAuditWriter>();
        _loggerMock = new Mock<IAppLogger>();

        _lockService = new LockService(_registry, _authzMock.Object, _auditMock.Object, _loggerMock.Object);
    }

    private static OwnerIdentity Alice => new("alice-001", "Alice", ClientKind.Wpf);
    private static IUserContext AdminUser => new SyntheticUserContext(
        userId: "admin-001", displayName: "Admin", clientKind: ClientKind.Wpf, roles: ["Administrator"]);
    private static IUserContext EngineerUser => new SyntheticUserContext(
        userId: "engineer-001", displayName: "Engineer", clientKind: ClientKind.Web, roles: ["Engineer"]);

    [Fact]
    public async Task ForceReleaseAsync_Should_Succeed_When_UserHasPermission()
    {
        _registry.TryAcquire("pipeline-1", Alice, LockKind.Trigger);
        _authzMock.Setup(a => a.CanAsync(It.IsAny<IUserContext>(), Permission.Pipeline_ForceRelease, "pipeline-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AuthDecision.Allow("admin"));

        var (success, _, error) = await _lockService.ForceReleaseAsync("pipeline-1", "Taking over for testing", AdminUser);

        Assert.True(success);
        Assert.Null(error);
        Assert.Null(_registry.Get("pipeline-1"));
    }

    [Fact]
    public async Task ForceReleaseAsync_Should_Deny_When_UserLacksPermission()
    {
        _registry.TryAcquire("pipeline-1", Alice, LockKind.Trigger);
        _authzMock.Setup(a => a.CanAsync(It.IsAny<IUserContext>(), Permission.Pipeline_ForceRelease, "pipeline-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AuthDecision.Deny("no-role", "You do not have force-release permission"));

        var (success, _, error) = await _lockService.ForceReleaseAsync("pipeline-1", "Reason", EngineerUser);

        Assert.False(success);
        Assert.Contains("permission", error!, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(_registry.Get("pipeline-1")); // Lock still held
    }

    [Fact]
    public async Task ForceReleaseAsync_Should_RejectEmptyReason()
    {
        _registry.TryAcquire("pipeline-1", Alice, LockKind.Trigger);

        var (success, _, error) = await _lockService.ForceReleaseAsync("pipeline-1", "", AdminUser);

        Assert.False(success);
        Assert.Contains("1 and 500", error!);
    }

    [Fact]
    public async Task ForceReleaseAsync_Should_RejectTooLongReason()
    {
        _registry.TryAcquire("pipeline-1", Alice, LockKind.Trigger);

        var longReason = new string('x', 501);
        var (success, _, error) = await _lockService.ForceReleaseAsync("pipeline-1", longReason, AdminUser);

        Assert.False(success);
        Assert.Contains("1 and 500", error!);
    }

    [Fact]
    public async Task ForceReleaseAsync_Should_Fail_When_NoActiveLock()
    {
        _authzMock.Setup(a => a.CanAsync(It.IsAny<IUserContext>(), Permission.Pipeline_ForceRelease, "pipeline-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AuthDecision.Allow("admin"));

        var (success, _, error) = await _lockService.ForceReleaseAsync("pipeline-1", "Reason", AdminUser);

        Assert.False(success);
        Assert.Contains("No active lock", error!);
    }

    [Fact]
    public async Task ForceReleaseAsync_Should_WriteAuditEntry_When_Successful()
    {
        _registry.TryAcquire("pipeline-1", Alice, LockKind.Trigger);
        _authzMock.Setup(a => a.CanAsync(It.IsAny<IUserContext>(), Permission.Pipeline_ForceRelease, "pipeline-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AuthDecision.Allow("admin"));

        await _lockService.ForceReleaseAsync("pipeline-1", "Emergency takeover", AdminUser);

        _auditMock.Verify(a => a.Enqueue(It.Is<AuditEntry>(e =>
            e.ActionName == "Pipeline_ForceRelease" &&
            e.ResourceId == "pipeline-1" &&
            e.UserId == "admin-001" &&
            e.CorrelationId!.Contains("Emergency takeover") &&
            e.CorrelationId!.Contains("alice-001")
        )), Times.Once);
    }

    [Fact]
    public async Task ForceReleaseAsync_Should_NotLogReasonViaAppLogger()
    {
        _registry.TryAcquire("pipeline-1", Alice, LockKind.Trigger);
        _authzMock.Setup(a => a.CanAsync(It.IsAny<IUserContext>(), Permission.Pipeline_ForceRelease, "pipeline-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AuthDecision.Allow("admin"));

        await _lockService.ForceReleaseAsync("pipeline-1", "Sensitive reason text here", AdminUser);

        // Verify AppLogger was called but reason is NOT in the message
        _loggerMock.Verify(l => l.Info("Lock", It.Is<string>(msg =>
            !msg.Contains("Sensitive reason text here")
        )), Times.Once);
    }
}
