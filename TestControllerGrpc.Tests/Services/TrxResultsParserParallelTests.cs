using System.Text;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// §3 guard: parsing a build folder now runs the per-file TRX parse in parallel
/// (ordered PLINQ). These tests assert the parallel path is DETERMINISTIC — the
/// aggregated <see cref="BuildNode"/> (use-case order, per-use-case totals, and
/// the flattened failure list) is identical across repeated runs and matches the
/// expected counts regardless of file-completion order.
/// </summary>
public class TrxResultsParserParallelTests : IDisposable
{
    private readonly string _buildDir;

    public TrxResultsParserParallelTests()
    {
        _buildDir = Path.Combine(Path.GetTempPath(), $"trx-par-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_buildDir);

        // Three use-case folders, each with several .trx files, so the parallel
        // parser has enough work to interleave completion order.
        for (int uc = 0; uc < 3; uc++)
        {
            var ucDir = Path.Combine(_buildDir, $"UseCase{uc:D2}");
            Directory.CreateDirectory(ucDir);
            for (int f = 0; f < 5; f++)
            {
                var path = Path.Combine(ucDir, $"result-{f:D2}.trx");
                File.WriteAllText(path, MakeTrx($"UseCase{uc:D2}", f), new UTF8Encoding(false));
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_buildDir)) Directory.Delete(_buildDir, recursive: true);
    }

    [Fact]
    public void ParseBuildFolder_Should_ProduceDeterministicResult_When_ParsedRepeatedly()
    {
        // Fresh parser each time so the per-instance cache does not mask ordering bugs.
        var first = new TrxResultsParser().ParseBuildFolder(_buildDir);

        for (int i = 0; i < 5; i++)
        {
            var again = new TrxResultsParser().ParseBuildFolder(_buildDir);
            AssertBuildsEqual(first, again);
        }
    }

    [Fact]
    public void ParseBuildFolder_Should_OrderUseCasesByName_And_AggregateCounts()
    {
        var build = new TrxResultsParser().ParseBuildFolder(_buildDir);

        Assert.Equal(3, build.UseCases.Count);
        Assert.Equal(new[] { "UseCase00", "UseCase01", "UseCase02" },
            build.UseCases.Select(u => u.UseCaseName).ToArray());

        // Each .trx has 2 tests (1 passed, 1 failed); 5 files × 3 use-cases = 15 files.
        Assert.Equal(30, build.TotalTests);
        Assert.Equal(15, build.PassedTests);
        Assert.Equal(15, build.FailedTests);
        Assert.Equal(15, build.AllFailedTests.Count);
    }

    [Fact]
    public void ParseDirectory_Should_ReturnStableOrder_When_ParsedRepeatedly()
    {
        var ucDir = Path.Combine(_buildDir, "UseCase00");

        var first = new TrxResultsParser().ParseDirectory(ucDir)
            .Select(r => r.FeatureName).ToList();

        for (int i = 0; i < 5; i++)
        {
            var again = new TrxResultsParser().ParseDirectory(ucDir)
                .Select(r => r.FeatureName).ToList();
            Assert.Equal(first, again);
        }
    }

    private static void AssertBuildsEqual(BuildNode a, BuildNode b)
    {
        Assert.Equal(a.TotalTests, b.TotalTests);
        Assert.Equal(a.PassedTests, b.PassedTests);
        Assert.Equal(a.FailedTests, b.FailedTests);
        Assert.Equal(a.UseCases.Count, b.UseCases.Count);
        Assert.Equal(
            a.UseCases.Select(u => u.UseCaseName).ToArray(),
            b.UseCases.Select(u => u.UseCaseName).ToArray());

        for (int i = 0; i < a.UseCases.Count; i++)
        {
            Assert.Equal(a.UseCases[i].Total, b.UseCases[i].Total);
            Assert.Equal(a.UseCases[i].Passed, b.UseCases[i].Passed);
            Assert.Equal(a.UseCases[i].Failed, b.UseCases[i].Failed);
        }

        Assert.Equal(
            a.AllFailedTests.Select(t => t.TestName).OrderBy(n => n).ToArray(),
            b.AllFailedTests.Select(t => t.TestName).OrderBy(n => n).ToArray());
    }

    private static string MakeTrx(string feature, int index) => $$"""
<?xml version="1.0" encoding="UTF-8"?>
<TestRun id="00000000-0000-0000-0000-000000000000" name="{{feature}}-{{index}}" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Times creation="2026-06-19T10:00:00.000Z" queuing="2026-06-19T10:00:00.000Z" start="2026-06-19T10:00:00.000Z" finish="2026-06-19T10:05:00.000Z" />
  <Results>
    <UnitTestResult testName="{{feature}}_{{index}}_Pass" outcome="Passed" duration="00:00:01.0000000" />
    <UnitTestResult testName="{{feature}}_{{index}}_Fail" outcome="Failed" duration="00:00:02.0000000">
      <Output>
        <ErrorInfo>
          <Message>boom {{index}}</Message>
          <StackTrace>at {{feature}}()</StackTrace>
        </ErrorInfo>
      </Output>
    </UnitTestResult>
  </Results>
</TestRun>
""";
}
