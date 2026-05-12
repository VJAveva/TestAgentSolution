using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

public class ExecutionLogCorrelatorTests
{
    [Fact]
    public void BuildReport_Should_ReturnError_When_BuildFolderMissing()
    {
        var config = new BuildResultsConfig { ResultsRootPath = @"C:\NonExistentRoot" };
        var correlator = new ExecutionLogCorrelator(config);

        var report = correlator.BuildReport("MissingBuild", "AnyTest");

        Assert.False(string.IsNullOrEmpty(report.Error));
        Assert.Contains("not found", report.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildReport_Should_ReturnError_When_TestNotFound()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "trx-test-" + Guid.NewGuid().ToString("N")[..8]);
        var buildFolder = Path.Combine(tempRoot, "Build001");
        Directory.CreateDirectory(buildFolder);
        try
        {
            var trxPath = Path.Combine(buildFolder, "results.trx");
            var trxXml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\">\n" +
                "  <Results>\n" +
                "    <UnitTestResult testName=\"OtherTest\" outcome=\"Passed\" />\n" +
                "  </Results>\n" +
                "</TestRun>";
            File.WriteAllText(trxPath, trxXml);

            var config = new BuildResultsConfig { ResultsRootPath = tempRoot };
            var correlator = new ExecutionLogCorrelator(config);

            var report = correlator.BuildReport("Build001", "MissingTest");

            Assert.Contains("not found", report.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void BuildReport_Should_ExtractTestData_When_TrxContainsTest()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "trx-test-" + Guid.NewGuid().ToString("N")[..8]);
        var buildFolder = Path.Combine(tempRoot, "Build001");
        Directory.CreateDirectory(buildFolder);
        try
        {
            var trxPath = Path.Combine(buildFolder, "results.trx");
            var trxXml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\">\n" +
                "  <Results>\n" +
                "    <UnitTestResult testName=\"MyTest\" outcome=\"Failed\"\n" +
                "                    startTime=\"2024-01-01T10:00:00Z\"\n" +
                "                    endTime=\"2024-01-01T10:05:00Z\"\n" +
                "                    duration=\"00:05:00\"\n" +
                "                    computerName=\"JVGR1\">\n" +
                "      <Output>\n" +
                "        <StdOut>Step 1: Init\nStep 2: Run\nStep 3 FAILED: Verify</StdOut>\n" +
                "        <ErrorInfo>\n" +
                "          <Message>TimeoutException at Step 3</Message>\n" +
                "          <StackTrace>at MyTest.Verify()</StackTrace>\n" +
                "        </ErrorInfo>\n" +
                "      </Output>\n" +
                "    </UnitTestResult>\n" +
                "  </Results>\n" +
                "</TestRun>";
            File.WriteAllText(trxPath, trxXml);

            var config = new BuildResultsConfig { ResultsRootPath = tempRoot };
            var correlator = new ExecutionLogCorrelator(config);

            var report = correlator.BuildReport("Build001", "MyTest");

            Assert.Equal("", report.Error);
            Assert.Equal("Failed", report.Outcome);
            Assert.Equal("JVGR1", report.Agent);
            Assert.Equal(3, report.FailedStepIndex);
            Assert.Contains("TimeoutException", report.ErrorMessage);
            Assert.NotEmpty(report.Steps);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void MergedLogLine_Should_HaveCorrectDefaults()
    {
        var line = new MergedLogLine();

        Assert.Equal("", line.Source);
        Assert.Equal("", line.Severity);
        Assert.Equal("", line.Message);
    }

    [Fact]
    public void TestStepInfo_Should_HaveCorrectDefaults()
    {
        var step = new TestStepInfo();

        Assert.Equal(0, step.Index);
        Assert.Equal("", step.Name);
        Assert.Equal("", step.Outcome);
    }

    [Fact]
    public void ExecutionLogReport_Should_HaveCorrectDefaults()
    {
        var report = new ExecutionLogReport();

        Assert.Equal("", report.BuildName);
        Assert.Equal("", report.TestCaseName);
        Assert.Equal(-1, report.FailedStepIndex);
        Assert.Empty(report.Steps);
        Assert.Empty(report.AgentLogLines);
        Assert.Empty(report.ControllerLogLines);
        Assert.Empty(report.MergedTimeline);
    }
}
