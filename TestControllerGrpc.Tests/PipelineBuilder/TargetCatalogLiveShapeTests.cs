using TestControllerGrpc.Core.PipelineBuilder;
using Xunit.Abstractions;

namespace TestControllerGrpc.Tests.PipelineBuilder;

/// <summary>
/// Runs the catalog over a COPY of the real deployed configs when they are reachable, because the
/// synthetic fixtures cannot prove the deny-list matches what production actually puts in a global
/// layer. Skips silently off the network so it never turns into a flaky gate.
/// </summary>
public class TargetCatalogLiveShapeTests(ITestOutputHelper output)
{
    private const string DeployedParameters = @"\\JVGR22\C$\TestControllerService\Parameters";

    [Fact]
    public void Catalog_Should_MatchTheDeployedConfigs_When_TheControllerIsReachable()
    {
        if (!Directory.Exists(DeployedParameters))
        {
            output.WriteLine($"skipped - '{DeployedParameters}' not reachable");
            return;
        }

        var staging = Path.Combine(Path.GetTempPath(), $"CatalogLive_{Guid.NewGuid():N}");
        try
        {
            foreach (var src in Directory.EnumerateFiles(DeployedParameters, "*.json", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(DeployedParameters, src);
                var dest = Path.Combine(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(src, dest);
            }

            var result = new DerivedTargetCatalog(staging).GetTargets();

            foreach (var t in result.Targets)
                output.WriteLine($"{t.Id}  release={t.ReleaseName}  profiles=[{string.Join(", ", t.Profiles)}]  keys={t.Values.Count}");
            foreach (var p in result.Problems)
                output.WriteLine($"PROBLEM: {p}");

            Assert.NotEmpty(result.Targets);
            Assert.Empty(result.Problems);

            // Every deployed config must yield a usable target, or the builder would offer a release
            // it cannot actually generate for.
            Assert.All(result.Targets, t =>
            {
                Assert.False(string.IsNullOrWhiteSpace(t.ReleaseName));
                Assert.NotEmpty(t.Values);
            });

            // The deny-list has to hold against real data, not just the fixtures.
            Assert.All(result.Targets, t =>
            {
                Assert.DoesNotContain("_BuildNumber", t.Values.Keys);
                Assert.DoesNotContain("_DropLocation", t.Values.Keys);
                Assert.DoesNotContain(t.Values.Keys, k => k.Contains("password", StringComparison.OrdinalIgnoreCase));
            });
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }
}
