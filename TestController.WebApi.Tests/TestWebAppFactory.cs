using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
              <WatchItem Tag="DisabledBuild" Path="C:\Trigger2" Filter="trigger2.txt" IsEnabled="false">
                <Event Type="Renamed" ExecutionType="Sequential" />
              </WatchItem>
            </WatchList>
            """);

        builder.ConfigureServices(services =>
        {
            // Remove ALL authentication-related registrations from the production pipeline.
            // AddNegotiate() registers handlers that require Kestrel's IConnectionItemsFeature,
            // which is unavailable in TestServer.
            var authDescriptors = services
                .Where(d =>
                    d.ServiceType.FullName?.Contains("Authentication") == true
                    || d.ServiceType.FullName?.Contains("Negotiate") == true
                    || d.ImplementationType?.FullName?.Contains("Negotiate") == true
                    || d.ImplementationType?.FullName?.Contains("TokenAuthentication") == true)
                .ToList();
            foreach (var d in authDescriptors)
                services.Remove(d);

            // Re-register authentication with only the test scheme
            services.AddAuthentication(opts =>
            {
                opts.DefaultAuthenticateScheme = "Test";
                opts.DefaultChallengeScheme = "Test";
                opts.DefaultScheme = "Test";
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", null);

            // Replace the mode provider so AdminRoleHandler can resolve roles from claims
            ReplaceService<TestController.Api.Security.IAuthenticationModeProvider>(services,
                _ => new TestAuthModeProvider());

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

    /// <summary>
    /// Creates an HttpClient whose requests carry a non-admin (User role) identity.
    /// Use this to test authorization-denied scenarios on Admin-only endpoints.
    /// Sends a special header that TestAuthHandler recognizes to switch to User role.
    /// </summary>
    public HttpClient CreateNonAdminClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "User");
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true); } catch { }
    }
}

/// <summary>
/// Authentication handler that always succeeds with a test user identity.
/// Used in integration tests to bypass Windows/Token authentication.
/// </summary>
internal sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Check X-Test-Role header for role override (used by CreateNonAdminClient)
        var role = "Admin";
        if (Request.Headers.TryGetValue("X-Test-Role", out var roleHeader))
            role = roleHeader.ToString();

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "test-user"),
            new Claim(ClaimTypes.Name, "TestUser"),
            new Claim(ClaimTypes.Role, role),
        };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, "Test");
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

/// <summary>
/// Test mode provider that resolves roles from standard ClaimTypes.Role claims.
/// </summary>
internal sealed class TestAuthModeProvider : TestController.Api.Security.IAuthenticationModeProvider
{
    public TestController.Api.Security.AuthMode Mode => TestController.Api.Security.AuthMode.Domain;
    public bool IsHealthy => true;

    public TestController.Api.Security.UserRole ResolveRole(ClaimsPrincipal user)
    {
        if (user.IsInRole("Admin"))
            return TestController.Api.Security.UserRole.Admin;
        if (user.Identity?.IsAuthenticated == true)
            return TestController.Api.Security.UserRole.User;
        return TestController.Api.Security.UserRole.Anonymous;
    }

    public string GetDiagnosticStatus() => "Test mode: all authenticated users are Admin";
}
