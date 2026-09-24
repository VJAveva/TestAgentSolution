extern alias AgentAlias;

using Microsoft.Extensions.Logging;
using TestControllerGrpc.Core.Maintenance;
using Detector = AgentAlias::TestAgentGrpc.Services.WindowsUpdateDetector;

namespace TestControllerGrpc.Tests.Maintenance;

/// <summary>
/// A failed pending-update scan used to report 0 and log at Debug, so a broken node was indistinguishable
/// from a clean one. These pin the honest reporting: no count without a successful scan, and never
/// "Up to date" on a scan that did not happen.
/// </summary>
public sealed class UpdateScanStatusTests
{
    private static string Json(object payload) =>
        System.Text.Json.JsonSerializer.Serialize(payload, WindowsUpdateEventMapper.JsonOptions);

    // ---- freshness classification -----------------------------------------

    [Fact]
    public void ClassifyFreshness_Should_ReturnStale_When_WindowsLastSearchIsOlderThanTheThreshold()
    {
        var now = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

        var status = Detector.ClassifyFreshness(now.AddDays(-10), now, staleAfterDays: 7);

        Assert.Equal(UpdateScanStatus.Stale, status);
    }

    [Fact]
    public void ClassifyFreshness_Should_ReturnOk_When_WindowsSearchedRecently()
    {
        var now = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

        var status = Detector.ClassifyFreshness(now.AddDays(-1), now, staleAfterDays: 7);

        Assert.Equal(UpdateScanStatus.Ok, status);
    }

    [Fact]
    public void ClassifyFreshness_Should_NotReportStale_When_WindowsLastSearchIsUnavailable()
    {
        // Unknown age is not old age - otherwise every node whose COM API omits the property reads as Stale.
        var now = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

        var status = Detector.ClassifyFreshness(null, now, staleAfterDays: 7);

        Assert.Equal(UpdateScanStatus.Ok, status);
    }

    // ---- state derivation --------------------------------------------------

    private static NodeMaintenanceEventDto Event(UpdateScanStatus scan, int? pending) => new()
    {
        NodeId = "JVGR1",
        Kind = pending > 0 ? MaintenanceEventKind.UpdatePending : MaintenanceEventKind.RebootCleared,
        Source = MaintenanceEventSource.StartupSnapshot,
        Status = new WindowsUpdateStatusDto { PendingCount = pending, ScanStatus = scan },
        DetectedUtc = DateTimeOffset.UtcNow,
    };

    [Theory]
    [InlineData(UpdateScanStatus.Failed)]
    [InlineData(UpdateScanStatus.Stale)]
    [InlineData(UpdateScanStatus.Unknown)]
    public void Apply_Should_ReportUnknown_When_TheScanCannotBeBelieved(UpdateScanStatus scan)
    {
        var store = new NodeUpdateStatusStore();

        store.Apply(Event(scan, pending: null));

        var status = store.Get("JVGR1");
        Assert.NotNull(status);
        Assert.Equal(WindowsUpdateState.Unknown, status!.State);
        Assert.Null(status.PendingCount);
    }

    [Fact]
    public void Apply_Should_ReportUpToDate_Only_When_TheScanSucceeded()
    {
        var store = new NodeUpdateStatusStore();

        store.Apply(Event(UpdateScanStatus.Ok, pending: 0));

        Assert.Equal(WindowsUpdateState.UpToDate, store.Get("JVGR1")!.State);
    }

    [Fact]
    public void Apply_Should_ReportPending_When_TheScanFoundUpdates()
    {
        var store = new NodeUpdateStatusStore();

        store.Apply(Event(UpdateScanStatus.Ok, pending: 4));

        Assert.Equal(WindowsUpdateState.UpdatePending, store.Get("JVGR1")!.State);
    }

    // ---- wire compatibility with an agent that predates these fields -------

    [Fact]
    public void TryMap_Should_YieldUnknown_When_AnOlderAgentOmitsTheScanFields()
    {
        // The old payload shape: no scanStatus, no timestamps. It must NOT be read as a healthy zero.
        string detail = Json(new
        {
            kind = (int)MaintenanceEventKind.RebootCleared,
            source = (int)MaintenanceEventSource.StartupSnapshot,
            rebootRequired = false,
            pendingCount = 0,
            detectedUtc = DateTimeOffset.UtcNow,
        });

        var mapped = WindowsUpdateEventMapper.TryMap("JVGR1", detail, DateTimeOffset.UtcNow);

        Assert.NotNull(mapped);
        Assert.Equal(UpdateScanStatus.Unknown, mapped!.Status.ScanStatus);

        var store = new NodeUpdateStatusStore();
        store.Apply(mapped);
        Assert.Equal(WindowsUpdateState.Unknown, store.Get("JVGR1")!.State);
    }

    [Fact]
    public void TryMap_Should_CarryTheScanFields_When_TheAgentSendsThem()
    {
        var scanned = new DateTimeOffset(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);
        string detail = Json(new
        {
            kind = (int)MaintenanceEventKind.RebootCleared,
            source = (int)MaintenanceEventSource.StartupSnapshot,
            rebootRequired = false,
            pendingCount = (int?)null,
            scanStatus = (int)UpdateScanStatus.Failed,
            lastScanUtc = (DateTimeOffset?)null,
            windowsLastSearchUtc = scanned,
            scanError = "0x80244007",
            detectedUtc = DateTimeOffset.UtcNow,
        });

        var mapped = WindowsUpdateEventMapper.TryMap("JVGR1", detail, DateTimeOffset.UtcNow);

        Assert.NotNull(mapped);
        Assert.Equal(UpdateScanStatus.Failed, mapped!.Status.ScanStatus);
        Assert.Null(mapped.Status.PendingCount);
        Assert.Equal(scanned, mapped.Status.WindowsLastSearchUtc);
        Assert.Equal("0x80244007", mapped.Status.ScanError);
    }

    // ---- failed scan -------------------------------------------------------

    /// <summary>Records what was logged so the test can assert the level, not just the return value.</summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Error)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }

    [Fact]
    public void ScanFailed_Should_ReportNoCountAndWarn_When_TheSearchThrows()
    {
        // The original bug in one test: a throwing scan logged at Debug and returned 0, so a node whose
        // update stack was broken looked exactly like a node with nothing to install.
        var logger = new RecordingLogger();
        var lastSearch = new DateTimeOffset(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);
        var error = new InvalidOperationException("0x80244007");

        var outcome = Detector.ScanFailed(logger, error, lastSearch);

        Assert.Equal(UpdateScanStatus.Failed, outcome.Status);
        Assert.Null(outcome.Count);
        Assert.Equal("0x80244007", outcome.Error);
        Assert.Empty(outcome.Items);
        Assert.Equal(lastSearch, outcome.WindowsLastSearchUtc);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Same(error, entry.Error);
    }

    // ---- dispatch must be unaffected ---------------------------------------

    [Theory]
    [InlineData(UpdateScanStatus.Ok)]
    [InlineData(UpdateScanStatus.Stale)]
    [InlineData(UpdateScanStatus.Failed)]
    [InlineData(UpdateScanStatus.Unknown)]
    public void Policy_Should_LeaveDispatchUnblocked_ForEveryScanStatus(UpdateScanStatus scan)
    {
        // Honesty about the scan must never become a reason to stop running pipelines.
        var store = new NodeUpdateStatusStore();
        store.Apply(Event(scan, pending: null));

        var effect = new UpdatePolicyEvaluator().Evaluate(store.Get("JVGR1")!, new UpdatePolicy());

        Assert.Equal(MaintenanceState.None, effect);
    }
}
