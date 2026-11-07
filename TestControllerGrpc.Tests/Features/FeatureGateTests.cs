using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Features;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Features;

public sealed class FeatureGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"FG_{Guid.NewGuid():N}");
    private readonly string _file;
    private readonly RecordingLogger _log = new();

    public FeatureGateTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "Features.json");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private FeatureGate Gate(string? json = null)
    {
        if (json is not null) File.WriteAllText(_file, json);
        return new FeatureGate(_file, _log, watch: false);
    }

    [Fact]
    public void IsEnabled_Should_BeTrueForEveryFeature_When_FileMissing()
    {
        using var gate = Gate();
        Assert.All(FeatureKeys.All, key => Assert.True(gate.IsEnabled(key)));
        Assert.Null(gate.LastError);
    }

    [Fact]
    public void IsEnabled_Should_BeTrue_When_KeyMissingFromFile()
    {
        using var gate = Gate("""{ "TeamName": "WAS-QA", "Features": { "CodeChurn": false } }""");
        Assert.True(gate.IsEnabled(FeatureKeys.Results));
        Assert.False(gate.IsEnabled(FeatureKeys.CodeChurn));
        Assert.Equal("WAS-QA", gate.TeamName);
    }

    [Fact]
    public void IsEnabled_Should_TurnOffFirReport_When_ResultsIsOff()
    {
        using var gate = Gate("""{ "Features": { "Results": false, "FirReport": true } }""");

        Assert.False(gate.IsEnabled(FeatureKeys.FirReport));
        var fir = gate.All().Single(f => f.Key == FeatureKeys.FirReport);
        Assert.True(fir.ConfiguredOn);
        Assert.Equal("off because Results is off", fir.OffBecause);
    }

    [Fact]
    public void IsEnabled_Should_TurnOffImpactMapping_When_CodeChurnIsOff()
    {
        using var gate = Gate("""{ "Features": { "CodeChurn": false } }""");
        Assert.False(gate.IsEnabled(FeatureKeys.ImpactMapping));
    }

    [Fact]
    public void Reload_Should_KeepLastGoodCopy_When_JsonIsBad()
    {
        using var gate = Gate("""{ "Features": { "CodeChurn": false } }""");
        File.WriteAllText(_file, """{ "Features": { "CodeChurn": tru""");

        gate.Reload();

        Assert.False(gate.IsEnabled(FeatureKeys.CodeChurn));
        Assert.NotNull(gate.LastError);
        Assert.Contains(_log.Warnings, w => w.Contains("keeping the last good"));
    }

    [Fact]
    public void Reload_Should_LogAndIgnore_When_KeyUnknownOrCore()
    {
        using var gate = Gate("""{ "Features": { "Bogus": false, "Pipelines": false } }""");

        Assert.Contains(_log.Warnings, w => w.Contains("Unknown feature key 'Bogus'"));
        Assert.Contains(_log.Warnings, w => w.Contains("'Pipelines' is a core feature"));
        Assert.All(FeatureKeys.All, key => Assert.True(gate.IsEnabled(key)));
    }

    [Fact]
    public void Reload_Should_RaiseChanged_When_FileChanges()
    {
        using var gate = Gate("""{ "Features": { } }""");
        var raised = 0;
        gate.Changed += () => raised++;

        File.WriteAllText(_file, """{ "Features": { "Reports": false } }""");
        gate.Reload();

        Assert.Equal(1, raised);
        Assert.False(gate.IsEnabled(FeatureKeys.Reports));
    }

    [Fact]
    public async Task Watcher_Should_ReloadWithoutRestart_When_FileEdited()
    {
        File.WriteAllText(_file, """{ "Features": { } }""");
        using var gate = new FeatureGate(_file, _log, watch: true);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.Changed += () => changed.TrySetResult();

        File.WriteAllText(_file, """{ "Features": { "GlobalVariables": false } }""");

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(gate.IsEnabled(FeatureKeys.GlobalVariables));
    }

    [Fact]
    public async Task GatedHostedService_Should_NotConstructOrStart_When_FeatureOff()
    {
        using var gate = Gate("""{ "Features": { "ImpactMapping": false } }""");
        await using var sp = new ServiceCollection().AddSingleton<IFeatureGate>(gate).BuildServiceProvider();
        CountingService.Constructed = 0;

        var svc = new FeatureGatedHostedService<CountingService>(sp, FeatureKeys.ImpactMapping);
        await svc.StartAsync(CancellationToken.None);

        Assert.Equal(0, CountingService.Constructed);
        Assert.Null(svc.Inner);
    }

    [Fact]
    public async Task GatedHostedService_Should_Start_When_FeatureOnOrNoGateRegistered()
    {
        await using var sp = new ServiceCollection().BuildServiceProvider();
        var svc = new FeatureGatedHostedService<CountingService>(sp, FeatureKeys.ImpactMapping);

        await svc.StartAsync(CancellationToken.None);

        Assert.True(svc.Inner!.Started);
    }

    public sealed class CountingService : IHostedService
    {
        public static int Constructed;
        public bool Started { get; private set; }
        public CountingService() => Interlocked.Increment(ref Constructed);
        public Task StartAsync(CancellationToken cancellationToken) { Started = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingLogger : IAppLogger
    {
        public List<string> Warnings { get; } = [];
#pragma warning disable CS0067
        public event Action<AppLogEntry>? EntryAdded;
#pragma warning restore CS0067
        public void Info(string category, string message) { }
        public void Warn(string category, string message) { lock (Warnings) Warnings.Add(message); }
        public void Error(string category, string message, Exception? ex = null) { }
        public void Log(LogLevel level, string category, string message, Exception? ex = null) { }
        public void Log(LogLevel level, string category, string message, string? correlationId, long elapsedMs = 0, Exception? ex = null) { }
        public void LogStructured(LogLevel level, string category, string message, string? agent = null, string? runId = null,
            string? pipeline = null, string? action = null, long elapsedMs = 0, Exception? ex = null) { }
        public IReadOnlyList<AppLogEntry> GetRecentEntries(int count = 500) => [];
    }
}
