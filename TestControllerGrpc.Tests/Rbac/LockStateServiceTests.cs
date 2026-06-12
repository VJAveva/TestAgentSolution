using Microsoft.Extensions.Options;
using TestController.Api.Contracts;
using TestControllerGrpc.Configuration;
using TestControllerGrpc.Identity;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Rbac;

public sealed class LockStateServiceTests
{
    private static PipelineLockDto MakeDto(string pipelineId, string owner = "ravi.kumar", string clientKind = "Web") => new()
    {
        PipelineId = pipelineId,
        OwnerDisplayName = owner,
        OwnerClientKind = clientKind,
        AcquiredUtc = DateTime.UtcNow.AddMinutes(-5),
        ExpiresUtc = DateTime.UtcNow.AddSeconds(25),
    };

    /// <summary>
    /// Minimal fake for testing LockStateService without real SignalR/HTTP.
    /// Exposes internal methods to simulate events.
    /// </summary>
    private sealed class TestableLockStateService : LockStateService
    {
        public TestableLockStateService(CurrentUserHolder holder) : base(holder)
        {
        }

        // Simulate event handlers by direct map manipulation + raise
        public void SimulateAcquired(PipelineLockDto dto)
        {
            GetAllMutable()[dto.PipelineId] = dto;
            RaiseLocksChangedDirect();
        }

        public void SimulateReleased(string pipelineId)
        {
            GetAllMutable().TryRemove(pipelineId, out _);
            RaiseLocksChangedDirect();
        }

        public void SimulateStolen(PipelineLockDto dto)
        {
            GetAllMutable()[dto.PipelineId] = dto;
            RaiseLocksChangedDirect();
        }

        // Access internal map for test assertions
        private System.Collections.Concurrent.ConcurrentDictionary<string, PipelineLockDto> GetAllMutable()
        {
            // Use reflection to access the private _locks field for testing
            var field = typeof(LockStateService).GetField("_locks",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (System.Collections.Concurrent.ConcurrentDictionary<string, PipelineLockDto>)field!.GetValue(this)!;
        }

        private void RaiseLocksChangedDirect()
        {
            // Bypass dispatcher for unit tests — invoke LocksChanged directly
            var evt = typeof(LockStateService).GetEvent("LocksChanged");
            var field = typeof(LockStateService).GetField("LocksChanged",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            // Use direct invoke pattern from the base class
            var locksChangedDelegate = (Action?)field?.GetValue(this);
            locksChangedDelegate?.Invoke();
        }
    }

    private static CurrentUserHolder MakeHolder(string displayName = "vinod.kumar", string userId = "vinod-001")
    {
        var holder = new CurrentUserHolder(
            new TestOptionsMonitor<RbacOptions>(new RbacOptions { Enabled = true }));
        holder.SetUser(new AuthUserInfo(
            UserId: userId,
            Username: displayName,
            DisplayName: displayName,
            Role: "Administrator",
            ClientKind: "Wpf",
            Capabilities: [],
            MustChangePassword: false,
            IsGuest: false,
            AssignedPipelineIds: []));
        return holder;
    }

    [Fact]
    public void GetLock_Should_ReturnNull_When_NoPipelineLocked()
    {
        var holder = MakeHolder();
        var service = new TestableLockStateService(holder);

        Assert.Null(service.GetLock("nonexistent"));
    }

    [Fact]
    public void GetLock_Should_ReturnDto_When_PipelineAcquired()
    {
        var holder = MakeHolder();
        var service = new TestableLockStateService(holder);
        var dto = MakeDto("pipeline-A");

        service.SimulateAcquired(dto);

        Assert.NotNull(service.GetLock("pipeline-A"));
        Assert.Equal("ravi.kumar", service.GetLock("pipeline-A")!.OwnerDisplayName);
    }

    [Fact]
    public void IsLockedByOther_Should_ReturnTrue_When_DifferentUserHoldsLock()
    {
        var holder = MakeHolder("vinod.kumar");
        var service = new TestableLockStateService(holder);
        service.SimulateAcquired(MakeDto("pipeline-A", "ravi.kumar"));

        Assert.True(service.IsLockedByOther("pipeline-A"));
    }

    [Fact]
    public void IsLockedByOther_Should_ReturnFalse_When_SameUserHoldsLock()
    {
        var holder = MakeHolder("vinod.kumar");
        var service = new TestableLockStateService(holder);
        service.SimulateAcquired(MakeDto("pipeline-A", "vinod.kumar"));

        Assert.False(service.IsLockedByOther("pipeline-A"));
    }

    [Fact]
    public void IsLockedByOther_Should_ReturnFalse_When_DefaultMode_DefaultUserOwns()
    {
        // Default mode: sole writer, badge should be collapsed (IsLockedByOther = false)
        var holder = new CurrentUserHolder(
            new TestOptionsMonitor<RbacOptions>(new RbacOptions { Enabled = false }));
        // holder defaults to "Default user" display name
        var service = new TestableLockStateService(holder);
        service.SimulateAcquired(MakeDto("pipeline-A", "Default user", "Wpf"));

        Assert.False(service.IsLockedByOther("pipeline-A"));
    }

    [Fact]
    public void IsLockedByMe_Should_ReturnTrue_When_SameUserHoldsLock()
    {
        var holder = MakeHolder("vinod.kumar");
        var service = new TestableLockStateService(holder);
        service.SimulateAcquired(MakeDto("pipeline-A", "vinod.kumar"));

        Assert.True(service.IsLockedByMe("pipeline-A"));
    }

    [Fact]
    public void SimulateReleased_Should_RemoveLockFromMap()
    {
        var holder = MakeHolder();
        var service = new TestableLockStateService(holder);
        service.SimulateAcquired(MakeDto("pipeline-A"));
        service.SimulateReleased("pipeline-A");

        Assert.Null(service.GetLock("pipeline-A"));
        Assert.False(service.IsLockedByOther("pipeline-A"));
    }

    [Fact]
    public void SimulateStolen_Should_UpdateOwnerInMap()
    {
        var holder = MakeHolder("vinod.kumar");
        var service = new TestableLockStateService(holder);
        service.SimulateAcquired(MakeDto("pipeline-A", "ravi.kumar"));

        // Force-release: now owned by vinod.kumar
        service.SimulateStolen(MakeDto("pipeline-A", "vinod.kumar", "Wpf"));

        Assert.False(service.IsLockedByOther("pipeline-A"));
        Assert.True(service.IsLockedByMe("pipeline-A"));
    }

    [Fact]
    public void LocksChanged_Should_Fire_When_LockAcquired()
    {
        var holder = MakeHolder();
        var service = new TestableLockStateService(holder);
        var fired = false;
        service.LocksChanged += () => fired = true;

        service.SimulateAcquired(MakeDto("pipeline-A"));

        Assert.True(fired);
    }

    [Fact]
    public void ConflictDialog_Should_NeverAppear_When_DefaultMode()
    {
        // In Default mode, WPF is sole writer. IsLockedByOther must always return false
        // for locks held by the Default user — so conflict dialog is never triggered.
        var holder = new CurrentUserHolder(
            new TestOptionsMonitor<RbacOptions>(new RbacOptions { Enabled = false }));
        var service = new TestableLockStateService(holder);

        // Default user locks a pipeline (WPF triggered it)
        service.SimulateAcquired(MakeDto("pipeline-A", "Default user", "Wpf"));

        // Should never indicate "locked by other" in Default mode
        Assert.False(service.IsLockedByOther("pipeline-A"));
    }
    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private readonly T _value;
        public TestOptionsMonitor(T value) => _value = value;
        public T CurrentValue => _value;
        public T Get(string? name) => _value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }}
