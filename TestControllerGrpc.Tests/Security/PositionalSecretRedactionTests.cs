using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Security;

/// <summary>
/// The revert action passes vCloud credentials POSITIONALLY:
///   cmd /c RevertRcloudMachine.bat AppServerPool2 jvgr1 someuser hunter2xyz
/// There is no "-Password" next to the value, so every named regex in SecurityRedactor is blind to it.
/// 156 credential-bearing lines reached app_*.log on JVGR22 before this was fixed.
/// </summary>
[Collection("TokenState")]
public class PositionalSecretRedactionTests : IDisposable
{
    private const string Secret = "hunter2xyz";
    private const string Org = "AppServerPool2";

    public PositionalSecretRedactionTests() => SecurityRedactor.ClearSecretValues();

    public void Dispose()
    {
        SecurityRedactor.ClearSecretValues();
        GC.SuppressFinalize(this);
    }

    private static PipelineExecutionContext ContextWithSecret(string key = "_RcloudPassword")
    {
        var ctx = new PipelineExecutionContext { WatchItemTag = "Sanity" };
        ParameterResolver.SetParameter(ctx, key, Secret, ParameterRank.Global);
        return ctx;
    }

    private static string RevertCommandLine() =>
        $@"cmd /c C:\TestSetup\RevertAgents\RevertRcloudMachine.bat {Org} jvgr1 someuser {Secret}";

    [Fact]
    public void Redact_Should_LeaveAPositionalSecret_When_ItWasNeverRegistered()
    {
        // Establishes the gap this feature closes: without registration there is nothing to match on.
        var redacted = SecurityRedactor.Redact(RevertCommandLine())!;

        Assert.Contains(Secret, redacted);
    }

    [Fact]
    public void Redact_Should_RemoveAPositionalSecret_When_TheParameterWasResolved()
    {
        ContextWithSecret();

        var redacted = SecurityRedactor.Redact(RevertCommandLine())!;

        Assert.DoesNotContain(Secret, redacted);
        Assert.Contains(SecurityRedactor.Redacted, redacted);
        // The surrounding diagnostic context must survive, or the log stops being useful.
        Assert.Contains(Org, redacted);
        Assert.Contains("jvgr1", redacted);
    }

    [Fact]
    public void RedactCommandLine_Should_RemoveAPositionalSecret_When_SplitAcrossCommandAndArguments()
    {
        ContextWithSecret();

        var redacted = SecurityRedactor.RedactCommandLine(
            @"C:\TestSetup\RevertAgents\RevertRcloudMachine.bat", $"{Org} jvgr1 someuser {Secret}");

        Assert.DoesNotContain(Secret, redacted);
    }

    [Fact]
    public void AppLogger_Should_NeverWriteAPositionalSecret_When_LoggingTheCommand()
    {
        ContextWithSecret();

        var dir = Path.Combine(Path.GetTempPath(), "redact-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var logger = new AppLogger("redact-test", dir);

        logger.Info("Action", $"RunCommand (local): {RevertCommandLine()}");

        var written = logger.GetRecentEntries().Single();
        Assert.DoesNotContain(Secret, written.Message);
        Assert.Contains(SecurityRedactor.Redacted, written.Message);
    }

    [Theory]
    [InlineData("_RcloudPassword")]
    [InlineData("_VCloudPassword")]
    [InlineData("_ApiToken")]
    [InlineData("MySecret")]
    [InlineData("db_pwd")]
    public void Redact_Should_RemoveTheValue_When_TheKeyNameMarksItSecret(string key)
    {
        ContextWithSecret(key);

        Assert.DoesNotContain(Secret, SecurityRedactor.Redact(RevertCommandLine())!);
    }

    [Fact]
    public void Register_Should_IgnoreAnUnresolvedToken_When_TheParameterNeverResolved()
    {
        var ctx = new PipelineExecutionContext { WatchItemTag = "Sanity" };
        ParameterResolver.SetParameter(ctx, "_RcloudPassword", "[_RcloudPassword]", ParameterRank.Global);

        // Scrubbing the placeholder would hide the very evidence that resolution failed.
        Assert.Equal(0, SecurityRedactor.TrackedSecretCount);
        Assert.Contains("[_RcloudPassword]", SecurityRedactor.Redact("value is [_RcloudPassword]")!);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("abc")]
    public void Register_Should_IgnoreShortValues_When_TheyWouldMangleOrdinaryText(string shortValue)
    {
        var ctx = new PipelineExecutionContext { WatchItemTag = "Sanity" };
        ParameterResolver.SetParameter(ctx, "_RcloudPassword", shortValue, ParameterRank.Global);

        Assert.Equal(0, SecurityRedactor.TrackedSecretCount);
    }

    [Fact]
    public void Register_Should_StillTrack_When_AHigherRankedValueWins()
    {
        // A losing value is still live in memory and can still be echoed by a script.
        var ctx = new PipelineExecutionContext { WatchItemTag = "Sanity" };
        ParameterResolver.SetParameter(ctx, "_RcloudPassword", "pinnedValue", ParameterRank.PipelinePin);
        ParameterResolver.SetParameter(ctx, "_RcloudPassword", Secret, ParameterRank.Global);

        Assert.Equal("pinnedValue", ctx.Parameters["_RcloudPassword"]);
        Assert.DoesNotContain(Secret, SecurityRedactor.Redact($"echo {Secret}")!);
        Assert.DoesNotContain("pinnedValue", SecurityRedactor.Redact("echo pinnedValue")!);
    }

    [Fact]
    public void Redact_Should_NotTouchANonSecretParameter_When_ItSharesTheCommandLine()
    {
        ContextWithSecret();
        var ctx = new PipelineExecutionContext { WatchItemTag = "Sanity" };
        ParameterResolver.SetParameter(ctx, "_BuildNumber", "OAK_main_20260619.6", ParameterRank.Global);

        var redacted = SecurityRedactor.Redact($"install OAK_main_20260619.6 using {Secret}")!;

        Assert.Contains("OAK_main_20260619.6", redacted);
        Assert.DoesNotContain(Secret, redacted);
    }
}
