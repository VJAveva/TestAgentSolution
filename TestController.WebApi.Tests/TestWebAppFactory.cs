using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TestController.WebApi.Services;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Tests;

/// <summary>
/// Custom factory that replaces file-system and gRPC-dependent services
/// with in-memory implementations suitable for testing.
/// </summary>
public class TestWebAppFactory : WebApplicationFactory<Program>
{
    /// <summary>Temp directory used for WatchList XML and TRX results.</summary>
    public string TempDir { get; } = Path.Combine(Path.GetTempPath(), $"WebApiTests_{Guid.NewGuid():N}");

    /// <summary>Path to the test WatchList XML file.</summary>
    public string WatchListFilePath => Path.Combine(TempDir, "WatchList.xml");

    /// <summary>Path to the test results root (for TRX parsing).</summary>
    public string ResultsRoot => Path.Combine(TempDir, "Results");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Ensure directories exist
        Directory.CreateDirectory(TempDir);
        Directory.CreateDirectory(ResultsRoot);

        // Write a minimal valid WatchList XML
        File.WriteAllText(WatchListFilePath, """
            <?xml version="1.0" encoding="utf-8"?>
            <WatchList>
              <WatchItem Tag="TestBuild" Path="C:\Trigger" Filter="trigger.txt">
                <Event Type="Renamed" ExecutionType="Sequential">
                  <Action Type="RunCommand" Command="echo" Parameters="hello" />
                </Event>
              </WatchItem>
              <WatchItem Tag="DisabledBuild" Path="C:\Trigger2" Filter="trigger2.txt">
                <Event Type="Renamed" ExecutionType="Sequential" />
              </WatchItem>
            </WatchList>
            """);

        builder.ConfigureServices(services =>
        {
            // Override configuration-based services with test-friendly versions
            ReplaceService<WatchListFileService>(services, sp =>
            {
                var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["VocabularyFile"] = WatchListFilePath,
                    })
                    .Build();
                var logger = sp.GetRequiredService<TestControllerGrpc.Services.IAppLogger>();
                var parser = sp.GetRequiredService<TestControllerGrpc.Services.IWatchListXmlParser>();
                return new WatchListFileService(config, parser, logger);
            });

            ReplaceService<AgentRegistry>(services, sp =>
            {
                var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Agents:0:Name"] = "Agent1",
                        ["Agents:0:Address"] = "http://localhost:15200",
                    })
                    .Build();
                return new AgentRegistry(config);
            });

            ReplaceService<BuildResultsConfig>(services, _ => new BuildResultsConfig
            {
                ResultsRootPath = ResultsRoot,
                GoodThreshold = 95.0,
                WarningThreshold = 85.0,
                ConsecutiveFailThreshold = 2,
            });
        });
    }

    /// <summary>Creates a test build folder with a .trx file under ResultsRoot.</summary>
    public void SeedBuildResult(string buildNumber, int total, int passed, int failed)
    {
        var buildDir = Path.Combine(ResultsRoot, buildNumber);
        Directory.CreateDirectory(buildDir);

        var trxContent = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Times start="2025-01-01T10:00:00" finish="2025-01-01T10:05:00" />
              <ResultSummary outcome="Completed">
                <Counters total="{total}" passed="{passed}" failed="{failed}" timeout="0" notExecuted="0" />
              </ResultSummary>
              <Results>
            {GenerateTestResults(total, passed, failed)}
              </Results>
            </TestRun>
            """;

        File.WriteAllText(Path.Combine(buildDir, "Feature1_Run.trx"), trxContent);
    }

    private static string GenerateTestResults(int total, int passed, int failed)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < passed; i++)
        {
            sb.AppendLine($"""    <UnitTestResult testName="PassTest{i}" outcome="Passed" duration="00:00:01" />""");
        }
        for (int i = 0; i < failed; i++)
        {
            sb.AppendLine($"""    <UnitTestResult testName="FailTest{i}" outcome="Failed" duration="00:00:01"><Output><ErrorInfo><Message>Assertion failed</Message></ErrorInfo></Output></UnitTestResult>""");
        }
        var remaining = total - passed - failed;
        for (int i = 0; i < remaining; i++)
        {
            sb.AppendLine($"""    <UnitTestResult testName="OtherTest{i}" outcome="NotExecuted" duration="00:00:00" />""");
        }
        return sb.ToString();
    }

    private static void ReplaceService<T>(IServiceCollection services, Func<IServiceProvider, T> factory) where T : class
    {
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(T));
        if (descriptor is not null)
            services.Remove(descriptor);
        services.AddSingleton(factory);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true); } catch { }
    }
}
