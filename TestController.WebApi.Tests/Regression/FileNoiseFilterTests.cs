using TestControllerGrpc.Ado;
using Xunit;

namespace TestController.WebApi.Tests.Regression;

/// <summary>
/// Verifies the modified-files noise filter drops package-manifest / pipeline files
/// (Universal-Package.json, *.yml) while keeping real source/interface changes.
/// </summary>
public class FileNoiseFilterTests
{
    [Theory]
    [InlineData("AppServer.MagellanPublic/Universal-Package.json", true)]
    [InlineData("Repos/Files/Universal-Packages.json", true)]
    [InlineData("pipelines/build.yml", true)]
    [InlineData("ExtInterfaces/ClassUtilities/SafeDllLoadHelper.cs", false)]
    public void IsIgnored_Should_FilterPackageManifestAndPipelineNoise(string path, bool ignored)
    {
        Assert.Equal(ignored, FileNoiseFilter.IsIgnored(path, null, new AdoOptions().IgnoredFilePatterns));
    }
}
