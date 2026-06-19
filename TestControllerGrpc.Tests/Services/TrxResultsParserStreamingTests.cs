using System.Text;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// D1: the hybrid TRX parser must produce identical results from the buffered
/// (XDocument) and streaming (XmlReader) paths for the same content. The path is
/// selected by <see cref="TrxResultsParser.StreamingThresholdBytes"/>, which these
/// tests force to exercise both branches on a small synthetic file.
/// </summary>
public class TrxResultsParserStreamingTests : IDisposable
{
    private readonly string _trxPath;

    public TrxResultsParserStreamingTests()
    {
        _trxPath = Path.Combine(Path.GetTempPath(), $"trx-stream-{Guid.NewGuid():N}.trx");
        File.WriteAllText(_trxPath, SampleTrx, new UTF8Encoding(false));
    }

    public void Dispose()
    {
        if (File.Exists(_trxPath)) File.Delete(_trxPath);
    }

    private static TrxTestRun ParseWith(string path, long threshold)
    {
        // Fresh parser per call so the per-instance file cache never serves a
        // result produced by the other path.
        var parser = new TrxResultsParser { StreamingThresholdBytes = threshold };
        return parser.ParseFile(path);
    }

    [Fact]
    public void ParseFile_Should_ProduceIdenticalResults_When_StreamingVsBuffered()
    {
        var buffered = ParseWith(_trxPath, long.MaxValue); // never streams
        var streamed = ParseWith(_trxPath, 0);             // always streams

        AssertRunsEqual(buffered, streamed);
    }

    [Fact]
    public void ParseFile_Should_UseBufferedPath_When_FileBelowThreshold()
    {
        // Default threshold (25MB) keeps the small synthetic file on the buffered path.
        var run = new TrxResultsParser().ParseFile(_trxPath);
        Assert.Equal(4, run.Total);
        Assert.Equal(1, run.Passed);
        Assert.Equal(1, run.Failed);
        Assert.Equal(1, run.Timeout);
        Assert.Equal(1, run.NotExecuted);
    }

    [Fact]
    public void ParseFileStreaming_Should_ParseCountersAndTimes_When_Forced()
    {
        var run = ParseWith(_trxPath, 0);

        Assert.Equal(4, run.Total);
        Assert.Equal(new DateTime(2026, 6, 19, 10, 0, 0, DateTimeKind.Utc).ToLocalTime(), run.StartTime);
        Assert.Equal(new DateTime(2026, 6, 19, 10, 5, 0, DateTimeKind.Utc).ToLocalTime(), run.EndTime);
    }

    [Fact]
    public void ParseFileStreaming_Should_ParseInnerResultsAndMergedOutput_When_Forced()
    {
        var run = ParseWith(_trxPath, 0);

        var beta = run.TestCases.Single(c => c.TestName == "Test_Beta");
        Assert.Equal("Failed", beta.Outcome);
        Assert.Equal("beta failed message", beta.ErrorMessage);
        Assert.Equal("at Beta()", beta.StackTrace);
        // StdErr merged into StdOut.
        Assert.Contains("beta stdout", beta.StdOut);
        Assert.Contains("beta stderr", beta.StdOut);
        // DebugTrace + TextMessages merged.
        Assert.Contains("beta debug", beta.DebugTrace);
        Assert.Contains("tm1", beta.DebugTrace);
        Assert.Contains("tm2", beta.DebugTrace);
        // InnerResults parsed as execution steps (not double-counted as top-level).
        Assert.Equal(2, beta.ExecutionSteps.Count);
        Assert.DoesNotContain(run.TestCases, c => c.TestName == "Beta.Step1");
    }

    private static void AssertRunsEqual(TrxTestRun a, TrxTestRun b)
    {
        Assert.Equal(a.FileName, b.FileName);
        Assert.Equal(a.FeatureName, b.FeatureName);
        Assert.Equal(a.StartTime, b.StartTime);
        Assert.Equal(a.EndTime, b.EndTime);
        Assert.Equal(a.Duration, b.Duration);
        Assert.Equal(a.Total, b.Total);
        Assert.Equal(a.Passed, b.Passed);
        Assert.Equal(a.Failed, b.Failed);
        Assert.Equal(a.Timeout, b.Timeout);
        Assert.Equal(a.NotExecuted, b.NotExecuted);
        Assert.Equal(a.TestCases.Count, b.TestCases.Count);

        for (int i = 0; i < a.TestCases.Count; i++)
        {
            var ca = a.TestCases[i];
            var cb = b.TestCases[i];
            Assert.Equal(ca.TestName, cb.TestName);
            Assert.Equal(ca.Outcome, cb.Outcome);
            Assert.Equal(ca.Duration, cb.Duration);
            Assert.Equal(ca.ErrorMessage, cb.ErrorMessage);
            Assert.Equal(ca.StackTrace, cb.StackTrace);
            Assert.Equal(ca.StdOut, cb.StdOut);
            Assert.Equal(ca.TrxFileName, cb.TrxFileName);
            Assert.Equal(ca.DebugTrace, cb.DebugTrace);
            Assert.Equal(ca.ExecutionSteps.Count, cb.ExecutionSteps.Count);

            for (int j = 0; j < ca.ExecutionSteps.Count; j++)
            {
                var sa = ca.ExecutionSteps[j];
                var sb = cb.ExecutionSteps[j];
                Assert.Equal(sa.StepName, sb.StepName);
                Assert.Equal(sa.Outcome, sb.Outcome);
                Assert.Equal(sa.Duration, sb.Duration);
                Assert.Equal(sa.StdOut, sb.StdOut);
                Assert.Equal(sa.ErrorMessage, sb.ErrorMessage);
            }
        }
    }

    private const string SampleTrx = """
<?xml version="1.0" encoding="UTF-8"?>
<TestRun id="00000000-0000-0000-0000-000000000000" name="sample" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Times creation="2026-06-19T10:00:00.000Z" queuing="2026-06-19T10:00:00.000Z" start="2026-06-19T10:00:00.000Z" finish="2026-06-19T10:05:00.000Z" />
  <Results>
    <UnitTestResult testName="Test_Alpha" outcome="Passed" duration="00:00:01.5000000">
      <Output>
        <StdOut>alpha stdout</StdOut>
      </Output>
    </UnitTestResult>
    <UnitTestResult testName="Test_Beta" outcome="Failed" duration="00:00:02.0000000">
      <Output>
        <StdOut>beta stdout</StdOut>
        <StdErr>beta stderr</StdErr>
        <DebugTrace>beta debug</DebugTrace>
        <ErrorInfo>
          <Message>beta failed message</Message>
          <StackTrace>at Beta()</StackTrace>
        </ErrorInfo>
        <TextMessages>
          <Message>tm1</Message>
          <Message>tm2</Message>
        </TextMessages>
      </Output>
      <InnerResults>
        <UnitTestResult testName="Beta.Step1" outcome="Passed" duration="00:00:00.5000000">
          <Output>
            <StdOut>step1 out</StdOut>
          </Output>
        </UnitTestResult>
        <UnitTestResult testName="Beta.Step2" outcome="Failed" duration="00:00:01.5000000">
          <Output>
            <ErrorInfo>
              <Message>step2 err</Message>
            </ErrorInfo>
          </Output>
        </UnitTestResult>
      </InnerResults>
    </UnitTestResult>
    <UnitTestResult testName="Test_Gamma" outcome="Timeout" duration="00:00:03.0000000" />
    <UnitTestResult testName="Test_Delta" outcome="NotExecuted" />
  </Results>
  <ResultSummary outcome="Failed">
    <Counters total="4" passed="1" failed="1" timeout="1" notExecuted="1" />
  </ResultSummary>
</TestRun>
""";
}
