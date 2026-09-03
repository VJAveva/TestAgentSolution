using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Ado;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Ado;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Tests.Impact;

public sealed class ImpactServiceCollectionExtensionsTests
{
    [Fact]
    public void AddImpactMapping_Should_ResolveOrchestrator_WithAllDefaults()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ImpactMapping:Index:DatabasePath"] = "test-impact-index.db",
                ["ImpactMapping:Learning:OutcomeDatabasePath"] = "test-impact-outcomes.db",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IAppLogger, NoopAppLogger>();
        services.AddSingleton<IAdoWorkItemClient, ThrowingAdo>(); // stand in for the host's AdoClient-backed client

        services.AddImpactMapping(config, ImpactHostRole.Reader);

        using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
        Assert.NotNull(provider.GetService<IImpactTestMappingService>());
    }

    [Fact]
    public void AddImpactMapping_ReaderWriter_Should_ValidateGraph_WithRealAdoClientChain()
    {
        // Mirrors the WebApi host: the real AdoImpactWorkItemClient resolves against a registered AdoClient,
        // and the ReaderWriter role adds the index-maintenance hosted service. ValidateOnBuild is the proxy
        // for "the host starts" — a missing registration fails here instead of at runtime.
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ImpactMapping:Index:DatabasePath"] = "test-rw-index.db",
                ["ImpactMapping:Learning:OutcomeDatabasePath"] = "test-rw-outcomes.db",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAppLogger, NoopAppLogger>();
        services.AddSingleton<IAdoTokenProvider, FakeTokenProvider>();
        services.Configure<AdoOptions>(_ => { });
        services.AddHttpClient<AdoClient>();

        services.AddImpactMapping(config, ImpactHostRole.ReaderWriter);

        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        Assert.NotNull(provider.GetService<IImpactTestMappingService>());
        Assert.NotEmpty(provider.GetServices<IHostedService>());
    }

    private sealed class FakeTokenProvider : IAdoTokenProvider
    {
        public Task<string> GetAuthHeaderAsync(CancellationToken ct) => Task.FromResult("Bearer test");
        public string Describe() => "fake";
    }

    private sealed class ThrowingAdo : IAdoWorkItemClient
    {
        public Task<IReadOnlyList<int>> QueryIdsAsync(string wiql, int maxResults, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<AdoWorkItemRef>> HydrateAsync(IReadOnlyCollection<int> ids, IReadOnlyCollection<string> fields, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<TestCaseCandidate>> GetTestCasesAsync(IReadOnlyCollection<int> ids, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<FeatureCandidate>> GetFeaturesAsync(IReadOnlyCollection<int> ids, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<int, int>> ResolveParentFeaturesAsync(IReadOnlyCollection<int> testCaseIds, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> GetChildTestCasesAsync(IReadOnlyCollection<int> featureIds, CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<AdoWorkItemRef> EnumerateChangedSinceAsync(string workItemType, DateTimeOffset since, int pageSize, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class NoopAppLogger : IAppLogger
    {
#pragma warning disable CS0067
        public event Action<AppLogEntry>? EntryAdded;
#pragma warning restore CS0067
        public void Log(LogLevel level, string category, string message, Exception? ex = null) { }
        public void Log(LogLevel level, string category, string message, string? correlationId, long elapsedMs = 0, Exception? ex = null) { }
        public void LogStructured(LogLevel level, string category, string message, string? agent = null, string? runId = null, string? pipeline = null, string? action = null, long elapsedMs = 0, Exception? ex = null) { }
        public void Info(string category, string message) { }
        public void Warn(string category, string message) { }
        public void Error(string category, string message, Exception? ex = null) { }
        public IReadOnlyList<AppLogEntry> GetRecentEntries(int count = 500) => [];
    }
}
