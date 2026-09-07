using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.Ado;
using TestControllerGrpc.Ado.Reporting;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Tests.Regression;

/// <summary>
/// The Code Churn policy pipeline is registered unconditionally but depends on IWorkItemQueries, which only
/// exists when ADO ingest is enabled. Both shapes must resolve, or the report silently loses its Features
/// section on one of the two hosts.
/// </summary>
public class CodeChurnPolicyRegistrationTests
{
    private static ServiceProvider Build(bool adoEnabled)
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ado:Enabled"] = adoEnabled ? "true" : "false",
            ["Ado:Organization"] = "org",
            ["Ado:Project"] = "proj",
            ["Ado:AuthMode"] = "ServicePrincipal",
        }).Build();

        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddSingleton<IAppLogger, NoopAppLogger>();
        services.AddAdoRegressionIngest();

        return services.BuildServiceProvider(validateScopes: true);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddAdoRegressionIngest_Should_ResolveReportBuilder(bool adoEnabled)
    {
        using ServiceProvider provider = Build(adoEnabled);

        Assert.NotNull(provider.GetService<ICodeChurnReportBuilder>());
        Assert.NotNull(provider.GetService<CodeChurnWorkItemPolicy>());
    }

    [Fact]
    public void HierarchyResolver_Should_BeNullObject_When_AdoDisabled()
    {
        using ServiceProvider provider = Build(adoEnabled: false);

        // Without ADO there is no hierarchy to read; the report must degrade, not throw.
        Assert.IsType<NullWorkItemHierarchyResolver>(provider.GetService<IWorkItemHierarchyResolver>());
    }

    [Fact]
    public void HierarchyResolver_Should_BeRealResolver_When_AdoEnabled()
    {
        using ServiceProvider provider = Build(adoEnabled: true);

        Assert.IsType<WorkItemHierarchyResolver>(provider.GetService<IWorkItemHierarchyResolver>());
    }

    [Fact]
    public void Registration_Should_Throw_When_PolicyConfigInvalid()
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ado:Enabled"] = "false",
            ["CodeChurn:WorkItemPolicy:MaxHierarchyDepth"] = "0",
        }).Build();

        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddSingleton<IAppLogger, NoopAppLogger>();
        services.AddAdoRegressionIngest();

        using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

        // Named-setting failure at first resolve rather than a silent clamp.
        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetService<CodeChurnWorkItemPolicy>());
        Assert.Contains("MaxHierarchyDepth", ex.Message);
    }

    private sealed class NoopAppLogger : IAppLogger
    {
        public void Log(Microsoft.Extensions.Logging.LogLevel level, string category, string message, Exception? ex = null) { }
        public void Log(Microsoft.Extensions.Logging.LogLevel level, string category, string message, string? correlationId, long elapsedMs = 0, Exception? ex = null) { }
        public void LogStructured(Microsoft.Extensions.Logging.LogLevel level, string category, string message, string? agent = null, string? runId = null, string? pipeline = null, string? action = null, long elapsedMs = 0, Exception? ex = null) { }
        public void Info(string category, string message) { }
        public void Warn(string category, string message) { }
        public void Error(string category, string message, Exception? ex = null) { }
        public IReadOnlyList<AppLogEntry> GetRecentEntries(int count = 500) => [];
        public event Action<AppLogEntry>? EntryAdded;
    }
}
