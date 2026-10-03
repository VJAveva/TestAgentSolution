using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Covers the operator-facing failure message. Written after a real incident on 2026-10-02: a copy script on
/// JVGR2 failed with six <c>[FAIL]</c> lines on STDOUT, while STDERR carried only three copies of
/// "The process tried to write to a nonexistent pipe" emitted beside PASSING steps by <c>echo F | xcopy</c>.
/// The old message tailed stderr blindly, so it reported the harmless pipe noise as the cause and hid all six
/// real failures - sending the investigation after a non-existent pipe bug for hours.
/// </summary>
public sealed class RemoteCommandFailureMessageTests
{
    private const string PipeNoise = "The process tried to write to a nonexistent pipe.";

    [Fact]
    public void DescribeFailure_Should_ReportStdoutFailures_When_StderrIsOnlyPipeNoise()
    {
        string[] stdout =
        [
            "[FAIL] QuickScript DLLs -> IDE -- xcopy error from C:\\Program Files (x86)\\ArchestrA\\Framework\\Bin\\ArchestrA.QuickScript*.dll",
            "[FAIL] Scripting DLLs -> IDE -- xcopy error from C:\\Program Files (x86)\\ArchestrA\\SEditorsCommon\\ArchestrA.Scripting*.dll",
            "[FAIL] QuickScript DLLs -> TestWindow -- xcopy error",
            "[FAIL] Scripting DLLs -> TestWindow -- xcopy error",
            "[FAIL] QuickScript DLLs -> ExecBase -- xcopy error",
            "[FAIL] Scripting DLLs -> ExecBase -- xcopy error",
        ];
        string[] stderr = [PipeNoise, PipeNoise, PipeNoise];

        var msg = RemoteCommandStreamRunner.DescribeFailure(1, stdout, stderr);

        Assert.Contains("Exit code 1 (General error)", msg, StringComparison.Ordinal);
        Assert.Contains("6 failure(s) on stdout", msg, StringComparison.Ordinal);
        Assert.Contains("ArchestrA", msg, StringComparison.Ordinal);
        Assert.DoesNotContain("nonexistent pipe", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescribeFailure_Should_CountEarlierFailures_When_MoreThanFiveOnStdout()
    {
        var stdout = Enumerable.Range(1, 8).Select(i => $"[FAIL] step {i}").ToArray();

        var msg = RemoteCommandStreamRunner.DescribeFailure(1, stdout, []);

        Assert.Contains("8 failure(s) on stdout", msg, StringComparison.Ordinal);
        Assert.Contains("(+3 earlier)", msg, StringComparison.Ordinal);
        Assert.Contains("step 8", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeFailure_Should_DropBenignNoise_When_RealStderrAlsoPresent()
    {
        string[] stderr = [PipeNoise, "Access is denied.", PipeNoise];

        var msg = RemoteCommandStreamRunner.DescribeFailure(5, [], stderr);

        Assert.Contains("Exit code 5 (Access denied)", msg, StringComparison.Ordinal);
        Assert.Contains("Access is denied.", msg, StringComparison.Ordinal);
        Assert.DoesNotContain("nonexistent pipe", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescribeFailure_Should_SayOutputWasIncidental_When_OnlyNoiseCaptured()
    {
        var msg = RemoteCommandStreamRunner.DescribeFailure(1, [], [PipeNoise, PipeNoise]);

        Assert.Contains("no failure detail", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("incidental", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("execution log", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescribeFailure_Should_SuggestInteractionCheck_When_NothingCaptured()
    {
        var msg = RemoteCommandStreamRunner.DescribeFailure(259, [], []);

        Assert.Contains("Exit code 259", msg, StringComparison.Ordinal);
        Assert.Contains("No output was captured", msg, StringComparison.Ordinal);
        Assert.Contains("UAC", msg, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[FAIL] QuickScript DLLs -> IDE", true)]
    [InlineData("[ERROR] could not copy", true)]
    [InlineData("xcopy failed with 4", true)]
    [InlineData("Access denied writing target", true)]
    [InlineData("System.IO.IOException: boom", true)]
    [InlineData("[PASS] Flex.loc", false)]
    [InlineData("[SKIP] ScriptPackage.dll -- source not found", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void LooksLikeFailure_Should_ClassifyScriptOutput(string line, bool expected) =>
        Assert.Equal(expected, RemoteCommandStreamRunner.LooksLikeFailure(line));

    [Fact]
    public void IsBenignNoise_Should_MatchTheBrokenPipeMessage()
    {
        Assert.True(RemoteCommandStreamRunner.IsBenignNoise(PipeNoise));
        Assert.False(RemoteCommandStreamRunner.IsBenignNoise("Access is denied."));
    }
}
