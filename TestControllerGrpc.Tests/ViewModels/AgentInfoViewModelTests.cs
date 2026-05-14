using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Tests.ViewModels;

/// <summary>
/// Tests for AgentInfoViewModel – connection status state machine,
/// status icon/color mapping, and detail line formatting.
/// </summary>
public class AgentInfoViewModelTests
{
    [Theory]
    [InlineData("Online",  "\u2714", "#FFA6E3A1")]
    [InlineData("Testing", "\u25B6", "#FF89B4FA")]
    [InlineData("Offline", "\u2716", "#FFF38BA8")]
    [InlineData("Error",   "\u26A0", "#FFFAB387")]
    [InlineData("Unknown", "\u2022", "#FF9399B2")]
    public void ConnectionStatus_Should_SetCorrectIconAndColor(
        string status, string expectedIcon, string expectedColor)
    {
        var vm = new AgentInfoViewModel { ConnectionStatus = status };
        Assert.Equal(expectedIcon, vm.StatusIcon);
        Assert.Equal(expectedColor, vm.StatusColor);
    }

    [Fact]
    public void Online_Should_ClearErrorDetail()
    {
        var vm = new AgentInfoViewModel { ErrorDetail = "some error" };
        vm.ConnectionStatus = "Online";
        Assert.Equal("", vm.ErrorDetail);
    }

    [Fact]
    public void Offline_Should_PreserveErrorDetail()
    {
        var vm = new AgentInfoViewModel { ErrorDetail = "timeout" };
        vm.ConnectionStatus = "Offline";
        Assert.Equal("timeout", vm.ErrorDetail);
    }

    // ─────────── UpdateDetailLine ───────────

    [Fact]
    public void UpdateDetailLine_Should_ShowMetrics_When_Online()
    {
        var vm = new AgentInfoViewModel
        {
            ConnectionStatus = "Online",
            AgentState = "Idle",
            CpuUsage = "25%",
            MemoryUsage = "60%",
            DiskFree = "120 GB",
        };
        vm.UpdateDetailLine();

        Assert.Contains("Idle", vm.DetailLine);
        Assert.Contains("CPU: 25%", vm.DetailLine);
        Assert.Contains("Mem: 60%", vm.DetailLine);
        Assert.Contains("Disk: 120 GB", vm.DetailLine);
    }

    [Fact]
    public void UpdateDetailLine_Should_ShowConnecting_When_Testing()
    {
        var vm = new AgentInfoViewModel { ConnectionStatus = "Testing" };
        vm.UpdateDetailLine();
        Assert.Equal("Connecting...", vm.DetailLine);
    }

    [Fact]
    public void UpdateDetailLine_Should_ShowUnreachable_When_OfflineNoError()
    {
        var vm = new AgentInfoViewModel { ConnectionStatus = "Offline" };
        vm.UpdateDetailLine();
        Assert.Equal("Unreachable", vm.DetailLine);
    }

    [Fact]
    public void UpdateDetailLine_Should_ShowErrorDetail_When_OfflineWithError()
    {
        var vm = new AgentInfoViewModel
        {
            ConnectionStatus = "Offline",
            ErrorDetail = "DNS resolution failed"
        };
        vm.UpdateDetailLine();
        Assert.Equal("DNS resolution failed", vm.DetailLine);
    }

    [Fact]
    public void UpdateDetailLine_Should_ShowConnectionError_When_ErrorNoDetail()
    {
        var vm = new AgentInfoViewModel { ConnectionStatus = "Error" };
        vm.UpdateDetailLine();
        Assert.Equal("Connection error", vm.DetailLine);
    }

    [Fact]
    public void UpdateDetailLine_Should_ShowNotTested_When_Unknown()
    {
        var vm = new AgentInfoViewModel { ConnectionStatus = "Unknown" };
        vm.UpdateDetailLine();
        Assert.Equal("Not tested", vm.DetailLine);
    }

    [Fact]
    public void UpdateDetailLine_Should_UpdateLastChecked()
    {
        var vm = new AgentInfoViewModel { ConnectionStatus = "Online" };
        vm.UpdateDetailLine();
        Assert.False(string.IsNullOrEmpty(vm.LastChecked));
    }

    // ─────────── Default state ───────────

    [Fact]
    public void Default_Should_StartUnknown()
    {
        var vm = new AgentInfoViewModel();
        Assert.Equal("Unknown", vm.ConnectionStatus);
        Assert.Equal("\u2022", vm.StatusIcon);
        Assert.Equal("#FF9399B2", vm.StatusColor);
    }
}
