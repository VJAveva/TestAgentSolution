using Microsoft.Extensions.Configuration;
using TestControllerGrpc.Core.Impact;

namespace TestController.WebApi.Tests.Impact;

/// <summary>
/// Path-resolution precedence for <see cref="ImpactIndexPathProvider"/>. The environment variable is
/// process-wide, so each test that sets it restores the previous value.
/// </summary>
public class ImpactIndexPathProviderTests : IDisposable
{
    private readonly string? _originalEnv =
        Environment.GetEnvironmentVariable(ImpactIndexPathProvider.EnvironmentVariableName);

    public void Dispose() =>
        Environment.SetEnvironmentVariable(ImpactIndexPathProvider.EnvironmentVariableName, _originalEnv);

    private static IConfiguration Config(string? indexRoot = null, string? learningRoot = null)
    {
        var values = new Dictionary<string, string?>();
        if (indexRoot is not null) values["ImpactMapping:IndexRoot"] = indexRoot;
        if (learningRoot is not null) values["ImpactMapping:LearningRoot"] = learningRoot;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void IndexRoot_Should_UseProgramDataDefault_When_NothingConfigured()
    {
        Environment.SetEnvironmentVariable(ImpactIndexPathProvider.EnvironmentVariableName, null);

        var provider = new ImpactIndexPathProvider(Config());

        Assert.Equal(ImpactIndexPathProvider.DefaultRoot, provider.IndexRoot);
        Assert.EndsWith("impact-index.db", provider.IndexFilePath);
    }

    [Fact]
    public void IndexRoot_Should_PreferConfiguration_Over_Default()
    {
        Environment.SetEnvironmentVariable(ImpactIndexPathProvider.EnvironmentVariableName, null);
        var configured = Path.Combine(Path.GetTempPath(), "impact-cfg");

        var provider = new ImpactIndexPathProvider(Config(configured));

        Assert.Equal(Path.GetFullPath(configured), provider.IndexRoot);
    }

    [Fact]
    public void IndexRoot_Should_PreferEnvironmentVariable_Over_Configuration()
    {
        var fromEnv = Path.Combine(Path.GetTempPath(), "impact-env");
        Environment.SetEnvironmentVariable(ImpactIndexPathProvider.EnvironmentVariableName, fromEnv);

        var provider = new ImpactIndexPathProvider(Config(Path.Combine(Path.GetTempPath(), "impact-cfg")));

        Assert.Equal(Path.GetFullPath(fromEnv), provider.IndexRoot);
    }

    [Fact]
    public void LearningRoot_Should_BeSiblingOfIndexRoot_SoRebuildCannotDeleteIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestAgentSolutionPathTest", "ImpactIndex");
        Environment.SetEnvironmentVariable(ImpactIndexPathProvider.EnvironmentVariableName, root);

        var provider = new ImpactIndexPathProvider(Config());

        Assert.NotEqual(provider.IndexRoot, provider.LearningRoot);
        Assert.False(provider.LearningRoot.StartsWith(provider.IndexRoot, StringComparison.OrdinalIgnoreCase),
            "learning store must not sit inside the index root, or -Force would delete run outcomes");
        Assert.EndsWith("impact-outcomes.db", provider.OutcomeFilePath);
    }

    [Fact]
    public void EnsureIndexRootExists_Should_BeIdempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestAgentSolutionPathTest-" + Guid.NewGuid().ToString("N"), "ImpactIndex");
        Environment.SetEnvironmentVariable(ImpactIndexPathProvider.EnvironmentVariableName, root);
        var provider = new ImpactIndexPathProvider(Config());

        try
        {
            provider.EnsureIndexRootExists();
            provider.EnsureIndexRootExists(); // must not throw on the second call

            Assert.True(Directory.Exists(provider.IndexRoot));
            Assert.True(Directory.Exists(provider.LearningRoot));
        }
        finally
        {
            var parent = Directory.GetParent(provider.IndexRoot)?.FullName;
            if (parent is not null && Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void IndexExists_Should_BeFalse_When_FileAbsent()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestAgentSolutionPathTest-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(ImpactIndexPathProvider.EnvironmentVariableName, root);

        var provider = new ImpactIndexPathProvider(Config());

        Assert.False(provider.IndexExists);
    }

    [Fact]
    public void LearningRoot_Should_HonourExplicitConfiguration()
    {
        Environment.SetEnvironmentVariable(ImpactIndexPathProvider.EnvironmentVariableName, null);
        var learning = Path.Combine(Path.GetTempPath(), "impact-learning-explicit");

        var provider = new ImpactIndexPathProvider(
            Config(indexRoot: Path.Combine(Path.GetTempPath(), "impact-idx"), learningRoot: learning));

        Assert.Equal(Path.GetFullPath(learning), provider.LearningRoot);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:\Cache\Impact")]
    [InlineData(@"C:\ProgramData\TestAgentSolution\ImpactIndex")]
    public void LearningRoot_Should_AlwaysBeAbsolute(string indexRoot)
    {
        // A relative learning path would resolve against the working directory, recreating the
        // per-host fragmentation this provider exists to remove.
        Environment.SetEnvironmentVariable(ImpactIndexPathProvider.EnvironmentVariableName, indexRoot);

        var provider = new ImpactIndexPathProvider(Config());

        Assert.True(Path.IsPathFullyQualified(provider.LearningRoot),
            $"learning root '{provider.LearningRoot}' is not absolute for index root '{indexRoot}'");
        Assert.True(Path.IsPathFullyQualified(provider.OutcomeFilePath));
    }
}
