using TestControllerGrpc.Core.Fir;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Fir;

/// <summary>
/// Shares the TokenState collection with the other suites that mutate SecurityRedactor's static secret
/// registry - asserting an exact secret count while another class registers values would race.
/// </summary>
[Collection("TokenState")]
public class FirRedactionTests : IDisposable
{
    private const string Jwt =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk";

    private const string PositionalSecret = "Str0ngP4ssw0rdXyz";

    public FirRedactionTests() => SecurityRedactor.ClearSecretValues();

    public void Dispose()
    {
        SecurityRedactor.ClearSecretValues();
        GC.SuppressFinalize(this);
    }

    private static FirTrxInput FailedWith(string stdOut) => new()
    {
        TestName = "S1_04_SmokeTestDeploy",
        ClassName = "WASSmokeTest.Set1.S1_04_SmokeTestDeploy",
        Outcome = "Failed",
        ErrorMessage = "Assert.IsFalse failed. TestCase FAILED.",
        StackTrace = "   at WASSmokeTest.Base.TestBase.TestCleanup() in C:\\src\\TestBase.cs:line 98",
        StdOut = stdOut,
    };

    [Fact]
    public void Mask_Should_HideAnAccessToken_When_TheLogPrintedAJwt()
    {
        var masked = FirRedactor.Mask($"[..] Info: (GetUserToken) {Jwt}");

        Assert.DoesNotContain(Jwt, masked);
        Assert.Contains(FirRedactor.Marker, masked);
    }

    [Fact]
    public void CountSecrets_Should_CountOnlyWhatMaskingAdded_When_TextAlreadyHoldsAMarker()
    {
        // The agent redacts on the wire, so a marker often arrives in the TRX already. Counting markers
        // in the output alone would report that pre-existing one as a fresh find.
        var text = $"line one {SecurityRedactor.Redacted}\nline two {Jwt}";

        Assert.Equal(1, FirRedactor.CountSecrets(text));
    }

    [Fact]
    public void CountSecrets_Should_ReturnZero_When_NothingIsSensitive()
        => Assert.Equal(0, FirRedactor.CountSecrets("Deployed 16 object(s) out of a total 16"));

    [Fact]
    public void Analyze_Should_MaskTheToken_When_ItLeakedIntoTheOutput()
    {
        var report = new FirAnalyzer().Analyze(FailedWith($"[..] Info: (GetUserToken) {Jwt}"))!;

        Assert.Equal(1, report.SecretsFound);
    }

    [Fact]
    public void Analyze_Should_MaskTheSecret_When_ItAppearsInTheFirstErrorLine()
    {
        var report = new FirAnalyzer().Analyze(FailedWith("Error: logon failed for token=abc123def456ghi789"))!;

        Assert.DoesNotContain("abc123def456ghi789", report.FirstErrorLine);
        Assert.Contains(FirRedactor.Marker, report.FirstErrorLine);
    }

    [Fact]
    public void Analyze_Should_StillMaskAToken_When_TheRuntimeRegistryIsEmpty()
    {
        // The registry is filled by ParameterResolver during a run; a report built from an old TRX sees
        // it empty, which is why the generic patterns have to stand on their own.
        Assert.Equal(0, SecurityRedactor.TrackedSecretCount);

        var report = new FirAnalyzer().Analyze(FailedWith($"Error: auth rejected {Jwt}"))!;

        Assert.DoesNotContain(Jwt, report.FirstErrorLine);
        Assert.Equal(1, report.SecretsFound);
    }

    [Fact]
    public void Analyze_Should_AlsoUseTheRuntimeRegistry_When_ASecretWasRegisteredDuringTheRun()
    {
        SecurityRedactor.RegisterSecretValue(PositionalSecret);

        var report = new FirAnalyzer().Analyze(FailedWith($"Error: logon failed for {PositionalSecret}"))!;

        Assert.DoesNotContain(PositionalSecret, report.FirstErrorLine);
    }

    [Fact]
    public void Analyze_Should_LeaveNoSecretInAnyReportString_When_TheOutputLeakedSeveral()
    {
        SecurityRedactor.RegisterSecretValue(PositionalSecret);

        var report = new FirAnalyzer().Analyze(FailedWith(
            $"""
             [..] Info: (GetUserToken) grpcuser: {PositionalSecret}
             [..] Info: (GetUserToken) {Jwt}
             Error: Failed to deploy REP1 : rejected token={Jwt}
             """))!;

        foreach (var text in new[] { report.Verdict, report.FirstErrorLine, report.Test, report.Computer })
        {
            Assert.DoesNotContain(Jwt, text);
            Assert.DoesNotContain(PositionalSecret, text);
        }
    }
}
