using TestController.Api.Controllers;
using TestController.Api.Security;

namespace TestController.WebApi.Tests.Security;

/// <summary>
/// Trigger parameters are substituted verbatim into an action's Command/Parameters, which reach
/// cmd.exe/powershell.exe as a raw argument string. Without these guards, Pipeline_Trigger permission
/// escalates to arbitrary code execution on the controller.
/// </summary>
public class TriggerParameterValidatorTests
{
    private static TriggerRequest WithParameter(string key, string value) =>
        new() { Parameters = new Dictionary<string, string> { [key] = value } };

    [Theory]
    [InlineData("&")]
    [InlineData("|")]
    [InlineData(";")]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData("^")]
    [InlineData("`")]
    [InlineData("\"")]
    public void Validate_Should_Reject_When_ValueContainsShellMetacharacter(string metacharacter)
    {
        var result = TriggerParameterValidator.Validate(WithParameter("_Build", $"1.0{metacharacter}calc.exe"));

        Assert.NotNull(result);
        Assert.Contains("_Build", result);
    }

    [Theory]
    [InlineData("$(calc.exe)")]
    [InlineData("@(calc.exe)")]
    public void Validate_Should_Reject_When_ValueContainsShellSubstitution(string payload)
    {
        Assert.NotNull(TriggerParameterValidator.Validate(WithParameter("_Build", payload)));
    }

    [Fact]
    public void Validate_Should_Reject_When_ValueContainsNewline()
    {
        // A newline forges an extra "Key,Value" line in the parameter file the trigger persists.
        var result = TriggerParameterValidator.Validate(WithParameter("_Build", "1.0\n_DropLocation,\\\\evil\\share"));

        Assert.NotNull(result);
        Assert.Contains("control character", result);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("has-dash")]
    [InlineData("has.dot")]
    [InlineData("")]
    public void Validate_Should_Reject_When_KeyIsNotWordCharacters(string key)
    {
        Assert.NotNull(TriggerParameterValidator.Validate(WithParameter(key, "safe")));
    }

    [Theory]
    [InlineData("_BuildNumber", "20250217.1")]
    [InlineData("_DropLocation", @"\\jvgr22\Builds\SP2026\Latest (x64)")]
    [InlineData("_AdminShare", @"\\jvgr1\C$\TestAgentService")]
    [InlineData("_Apostrophe", @"\\srv\John's Builds")]
    [InlineData("_EmailAddress", "a@b.com,c@d.com")]
    [InlineData("_Percent", "100% complete")]
    [InlineData("_Path", "C:/builds/drop-1")]
    public void Validate_Should_Allow_When_ValueIsRealisticPipelineInput(string key, string value)
    {
        // Guards against over-blocking: these shapes appear in real WatchList parameter files and deploy paths.
        Assert.Null(TriggerParameterValidator.Validate(WithParameter(key, value)));
    }

    [Fact]
    public void Validate_Should_Reject_When_BuildNumberContainsMetacharacter()
    {
        Assert.NotNull(TriggerParameterValidator.Validate(new TriggerRequest { BuildNumber = "1.0 & calc.exe" }));
    }

    [Fact]
    public void Validate_Should_Reject_When_DropLocationContainsMetacharacter()
    {
        Assert.NotNull(TriggerParameterValidator.Validate(new TriggerRequest { DropLocation = @"\\srv\s | calc.exe" }));
    }

    [Fact]
    public void Validate_Should_Reject_When_ValueExceedsLengthLimit()
    {
        Assert.NotNull(TriggerParameterValidator.Validate(WithParameter("_Big", new string('a', 2049))));
    }

    [Fact]
    public void Validate_Should_Reject_When_TooManyParameters()
    {
        var many = Enumerable.Range(0, 129).ToDictionary(i => $"_K{i}", _ => "v");

        Assert.NotNull(TriggerParameterValidator.Validate(new TriggerRequest { Parameters = many }));
    }

    [Fact]
    public void Validate_Should_Allow_When_RequestIsNullOrEmpty()
    {
        Assert.Null(TriggerParameterValidator.Validate(null));
        Assert.Null(TriggerParameterValidator.Validate(new TriggerRequest()));
    }
}
