using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Ado;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Tests.Regression;

public sealed class CodeChurnTeamConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"CCT_{Guid.NewGuid():N}");

    public CodeChurnTeamConfigTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Map(string json)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static IOptions<AdoOptions> Options(Action<AdoOptions> configure)
    {
        var o = new AdoOptions();
        configure(o);
        return Microsoft.Extensions.Options.Options.Create(o);
    }

    private const string TwoTeamRepos = """
        {
          "vobs": [
            { "name": "Alpha", "buildDef": 101, "repository": "TeamX.Alpha" },
            { "name": "Beta",  "buildDef": 202, "repository": "TeamX.Beta" }
          ],
          "components": [ { "id": "TeamX.Alpha" }, { "id": "TeamX.Beta" } ]
        }
        """;

    [Fact]
    public void ComponentBuildMap_Should_LoadTheTeamsRepositories_When_ComponentMapPathConfigured()
    {
        var map = new ComponentBuildMap(new SilentLogger(), Options(o => o.ComponentMapPath = Map(TwoTeamRepos)));

        Assert.Equal(["TeamX.Alpha", "TeamX.Beta"], map.All().Select(c => c.Repository).OrderBy(r => r));
    }

    [Fact]
    public void ConnectionInfo_Should_SayNoRepositoriesConfigured_When_MapIsEmpty()
    {
        var options = Options(o => o.ComponentMapPath = Map("""{ "vobs": [], "components": [] }"""));
        var catalog = new RegressionSourceCatalog(options, new ComponentBuildMap(new SilentLogger(), options),
            new ServiceCollection().BuildServiceProvider());

        Assert.Equal(RegressionSourceCatalog.NoRepositoriesMessage, catalog.GetConnectionInfo().Warning);
    }

    [Fact]
    public void ConnectionInfo_Should_HaveNoWarning_When_RepositoriesConfigured()
    {
        var options = Options(o => o.ComponentMapPath = Map(TwoTeamRepos));
        var catalog = new RegressionSourceCatalog(options, new ComponentBuildMap(new SilentLogger(), options),
            new ServiceCollection().BuildServiceProvider());

        Assert.Null(catalog.GetConnectionInfo().Warning);
    }

    [Theory]
    [InlineData("TeamX.", "Alpha", "TeamX.Alpha")]
    [InlineData("", "Alpha", null)]
    public void RepositoryResolver_Should_UseTheConfiguredPrefix_When_HintIsShort(string prefix, string hint, string? expected)
    {
        var resolver = new RepositoryResolver(new SilentLogger(),
            Options(o => { o.ComponentMapPath = Map(TwoTeamRepos); o.RepositoryPrefix = prefix; }));

        Assert.Equal(expected, resolver.ResolveComponent(hint));
    }

    [Fact]
    public void ConnectionInfo_Should_NeverContainTheCredentialValue_When_PatIsSet()
    {
        const string secret = "s3cr3t-pat-value-do-not-leak";
        var envName = $"CCT_PAT_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(envName, secret);
        try
        {
            var options = Options(o =>
            {
                o.Enabled = true;
                o.AuthMode = AdoAuthMode.Pat;
                o.SecretEnvVarName = envName;
                o.ComponentMapPath = Map(TwoTeamRepos);
            });
            var catalog = new RegressionSourceCatalog(options, new ComponentBuildMap(new SilentLogger(), options),
                new ServiceCollection().BuildServiceProvider());

            var info = catalog.GetConnectionInfo();

            Assert.True(info.CredentialConfigured);
            Assert.DoesNotContain(secret, JsonSerializer.Serialize(info));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    private sealed class SilentLogger : IAppLogger
    {
#pragma warning disable CS0067
        public event Action<AppLogEntry>? EntryAdded;
#pragma warning restore CS0067
        public void Log(LogLevel level, string category, string message, Exception? ex = null) { }
        public void Log(LogLevel level, string category, string message, string? correlationId, long elapsedMs = 0, Exception? ex = null) { }
        public void LogStructured(LogLevel level, string category, string message, string? agent = null, string? runId = null,
            string? pipeline = null, string? action = null, long elapsedMs = 0, Exception? ex = null) { }
        public void Info(string category, string message) { }
        public void Warn(string category, string message) { }
        public void Error(string category, string message, Exception? ex = null) { }
        public IReadOnlyList<AppLogEntry> GetRecentEntries(int count = 500) => [];
    }
}
