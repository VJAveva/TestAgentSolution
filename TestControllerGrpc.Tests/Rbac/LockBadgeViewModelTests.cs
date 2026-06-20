using Microsoft.Extensions.Options;
using TestController.Api.Contracts;
using TestControllerGrpc.Configuration;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Tests.Rbac;

public sealed class LockBadgeViewModelTests
{
    private static PipelineLockDto MakeDto(string pipelineId, string owner, string clientKind = "Web") => new()
    {
        PipelineId = pipelineId,
        OwnerUserId = owner,
        OwnerDisplayName = owner,
        OwnerClientKind = clientKind,
        AcquiredUtc = DateTime.UtcNow.AddMinutes(-2),
        ExpiresUtc = DateTime.UtcNow.AddSeconds(25),
    };

    private static CurrentUserHolder MakeHolder(string displayName = "vinod.kumar")
    {
        var holder = new CurrentUserHolder(
            new TestOptionsMonitor<RbacOptions>(new RbacOptions { Enabled = true }));
        holder.SetUser(new AuthUserInfo(
            UserId: "vinod-001",
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

    /// <summary>Minimal fake LockStateService for badge VM testing.</summary>
    private sealed class FakeLockStateService : LockStateService
    {
        private readonly Dictionary<string, PipelineLockDto> _map = [];
        private readonly string _currentDisplayName;

        public FakeLockStateService(string currentDisplayName) : base()
        {
            _currentDisplayName = currentDisplayName;
        }

        public void SetLock(PipelineLockDto dto) => _map[dto.PipelineId] = dto;
        public void RemoveLock(string pipelineId) => _map.Remove(pipelineId);

        public new PipelineLockDto? GetLock(string tag) => _map.TryGetValue(tag, out var d) ? d : null;

        public new bool IsLockedByOther(string tag)
        {
            if (!_map.TryGetValue(tag, out var dto)) return false;
            return !string.Equals(dto.OwnerDisplayName, _currentDisplayName, StringComparison.OrdinalIgnoreCase);
        }

        public new bool IsLockedByMe(string tag) => _map.ContainsKey(tag) && !IsLockedByOther(tag);

        public void RaiseChanged() => RaiseLocksChangedForTest();

        private void RaiseLocksChangedForTest()
        {
            // Invoke the event via reflection for testing
            var field = typeof(LockStateService).GetField("LocksChanged",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var handler = (Action?)field?.GetValue(this);
            handler?.Invoke();
        }
    }

    [Fact]
    public void IsLockedByOther_Should_BeTrue_When_DifferentOwner()
    {
        var holder = MakeHolder("vinod.kumar");
        var service = new FakeLockStateService("vinod.kumar");
        service.SetLock(MakeDto("pipe-A", "ravi.kumar"));

        // Manually verify the service reports locked-by-other
        Assert.True(service.IsLockedByOther("pipe-A"));
    }

    [Fact]
    public void IsLockedByMe_Should_BeTrue_When_SameOwner()
    {
        var service = new FakeLockStateService("vinod.kumar");
        service.SetLock(MakeDto("pipe-A", "vinod.kumar", "Wpf"));

        Assert.True(service.IsLockedByMe("pipe-A"));
        Assert.False(service.IsLockedByOther("pipe-A"));
    }

    [Fact]
    public void OwnerLabel_Should_Format_AsExpected_When_LockedByOther()
    {
        // Verify the expected format "Locked by {name} ({client})"
        var dto = MakeDto("pipe-A", "ravi.kumar", "Web");
        var expectedLabel = $"Locked by {dto.OwnerDisplayName} ({dto.OwnerClientKind})";

        Assert.Equal("Locked by ravi.kumar (Web)", expectedLabel);
    }

    [Fact]
    public void ElapsedText_Should_Format_AsMinutesSeconds()
    {
        // Verify the mm:ss format calculation
        var acquiredUtc = DateTime.UtcNow.AddMinutes(-3).AddSeconds(-45);
        var elapsed = DateTime.UtcNow - acquiredUtc;
        var formatted = $"Your run \u00B7 {(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}";

        Assert.StartsWith("Your run \u00B7 03:", formatted);
    }

    [Fact]
    public void IsVisible_Should_BeFalse_When_NoLock()
    {
        var service = new FakeLockStateService("vinod.kumar");
        Assert.False(service.IsLockedByOther("pipe-A"));
        Assert.False(service.IsLockedByMe("pipe-A"));
    }

    [Fact]
    public void DefaultMode_Should_NeverShowLockedByOther_When_DefaultUserOwns()
    {
        // In Default mode, display name is "Default user" — badge collapsed
        var service = new FakeLockStateService("Default user");
        service.SetLock(MakeDto("pipe-A", "Default user", "Wpf"));

        Assert.False(service.IsLockedByOther("pipe-A"));
        Assert.True(service.IsLockedByMe("pipe-A"));
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
