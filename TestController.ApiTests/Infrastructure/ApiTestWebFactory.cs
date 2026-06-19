using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TestController.Api.Security;
using TestController.WebApi.Services;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.ApiTests.Infrastructure;

/// <summary>
/// Boots TestController.WebApi in-process for InMemory mode. Replaces the three
/// services that cannot run under TestServer or that must be deterministic:
///   1. Authentication  -> <see cref="TestAuthHandler"/> (Negotiate can't run here).
///   2. WatchListFileService -> points at a temp XML with a known enabled tag
///      (the same service backs StandaloneVocabularyMonitor.CurrentConfig, which
///      the /api/execution/trigger path reads).
///   3. IAgentGrpcDispatcher -> <see cref="FakeAgentDispatcher"/> (no real agents).
/// Everything else (executor, session manager, locks, RBAC) runs for real.
/// </summary>
public sealed class ApiTestWebFactory : WebApplicationFactory<Program>
{
    public string TempDir { get; } =
        Path.Combine(Path.GetTempPath(), $"ApiTests_{Guid.NewGuid():N}");

    public string WatchListFilePath => Path.Combine(TempDir, "WatchList.xml");
    public string ResultsRoot => Path.Combine(TempDir, "Results");

    /// <summary>The deterministic agent seam. Tune behavior per test.</summary>
    public FakeAgentDispatcher Fake { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(TempDir);
        Directory.CreateDirectory(ResultsRoot);

        // A minimal valid WatchList with one enabled local-command tag.
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
            // 1. Strip production authentication (Negotiate requires Kestrel features).
            var authDescriptors = services
                .Where(d =>
                    d.ServiceType.FullName?.Contains("Authentication") == true
                    || d.ServiceType.FullName?.Contains("Negotiate") == true
                    || d.ImplementationType?.FullName?.Contains("Negotiate") == true
                    || d.ImplementationType?.FullName?.Contains("TokenAuthentication") == true)
                .ToList();
            foreach (var d in authDescriptors)
                services.Remove(d);

            services.AddAuthentication(opts =>
            {
                opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                opts.DefaultScheme = TestAuthHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);

            ReplaceSingleton<IAuthenticationModeProvider>(services, _ => new TestAuthModeProvider());

            // 2. Point WatchList loading at the temp XML (drives trigger + /api/watchlist).
            ReplaceSingleton<WatchListFileService>(services, sp =>
            {
                var config = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["VocabularyFile"] = WatchListFilePath,
                    })
                    .Build();
                return new WatchListFileService(
                    config,
                    sp.GetRequiredService<IWatchListXmlParser>(),
                    sp.GetRequiredService<IAppLogger>());
            });

            // 3. Deterministic agent dispatcher.
            ReplaceSingletonInstance<IAgentGrpcDispatcher>(services, Fake);

            // Keep results/agent registry self-contained (no machine paths / live agents).
            ReplaceSingleton(services, _ => new BuildResultsConfig
            {
                ResultsRootPath = ResultsRoot,
                GoodThreshold = 95.0,
                WarningThreshold = 85.0,
                ConsecutiveFailThreshold = 2,
            });
        });
    }

    private static void ReplaceSingleton<T>(IServiceCollection services, Func<IServiceProvider, T> factory)
        where T : class
    {
        Remove<T>(services);
        services.AddSingleton(factory);
    }

    private static void ReplaceSingletonInstance<T>(IServiceCollection services, T instance)
        where T : class
    {
        Remove<T>(services);
        services.AddSingleton(instance);
    }

    private static void Remove<T>(IServiceCollection services)
    {
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(T));
        if (descriptor is not null)
            services.Remove(descriptor);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }
}
