using System.Globalization;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels;
using TestControllerGrpc.ViewModels.Converters;

namespace TestControllerGrpc.Tests.ViewModels;

/// <summary>
/// The tree tooltip is fed ALREADY-RESOLVED text. It used to resolve again, which re-marked an
/// unresolved token and rendered "[_Agent1] (not set) (not set)" to the user.
/// </summary>
[Collection("TokenState")]
public class NodeTooltipConverterTests : IDisposable
{
    public NodeTooltipConverterTests()
    {
        TreeNodeViewModel.ClearTokenScopes();
        TreeNodeViewModel.ClearSessionValues();
    }

    public void Dispose()
    {
        TreeNodeViewModel.ClearTokenScopes();
        TreeNodeViewModel.ClearSessionValues();
        GC.SuppressFinalize(this);
    }

    private static string Convert(
        string command = "", string parameters = "", string agent = "",
        string status = "", string provenance = "")
        => (string)new NodeTooltipConverter().Convert(
            ["Action", "RunRemoteCommand", command, parameters, agent, status, provenance],
            typeof(string), null, CultureInfo.InvariantCulture);

    [Fact]
    public void Tooltip_Should_MarkAMissingTokenOnce_When_TheTextArrivesAlreadyResolved()
    {
        var resolved = TokenDisplay.Resolve("[_Agent1]", _ => null).Text;
        Assert.Equal("[_Agent1]" + TokenDisplay.NotSet, resolved);

        var tip = Convert(parameters: resolved);

        Assert.Contains("[_Agent1]" + TokenDisplay.NotSet, tip);
        Assert.DoesNotContain(TokenDisplay.NotSet + TokenDisplay.NotSet, tip);
    }

    [Fact]
    public void Tooltip_Should_NotReResolve_When_AValueLooksLikeAToken()
    {
        // A real value containing brackets must survive verbatim.
        var tip = Convert(command: @"C:\builds\[archive]\run.bat");

        Assert.Contains(@"C:\builds\[archive]\run.bat", tip);
        Assert.DoesNotContain(TokenDisplay.NotSet, tip);
    }

    [Fact]
    public void Tooltip_Should_ShowValueAndLayer_When_ProvenanceIsSupplied()
    {
        var tip = Convert(
            parameters: "OAK_main_20260619.6",
            provenance: "[_BuildNumber] \u2192 OAK_main_20260619.6 (Profile)");

        Assert.Contains("Tokens:", tip);
        Assert.Contains("[_BuildNumber] \u2192 OAK_main_20260619.6 (Profile)", tip);
    }

    [Fact]
    public void Tooltip_Should_OmitTheTokenSection_When_TheFieldsHoldNoTokens()
    {
        var tip = Convert(command: "run.bat", provenance: "");

        Assert.DoesNotContain("Tokens:", tip);
    }

    [Fact]
    public void Tooltip_Should_FallBackToStatus_When_TheNodeIsNotAnAction()
    {
        var tip = (string)new NodeTooltipConverter().Convert(
            ["WatchItem", "", "", "", "", "Completed", ""],
            typeof(string), null, CultureInfo.InvariantCulture);

        Assert.Equal("Completed", tip);
    }
}
