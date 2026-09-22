using System.Text;
using Moq;
using TestControllerGrpc.Core.Maintenance;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Maintenance;

/// <summary>
/// The installer's results arrive over the dispatcher's OutputReceived event rather than in ActionResult, so
/// the cases that matter are the ones where a wrong answer is silent: another agent's output, a missing result
/// line, and an install that partially failed but still exited zero.
/// </summary>
public class NodeUpdateInstallerTests
{
    private const string Node = "JVGR2";
    private const string Sentinel = "##TCWU##";

    private static string Line(string json) => $"{Sentinel}{json}{Sentinel}";

    /// <summary>Raises OutputReceived from inside the dispatch, which is the only window the installer listens in.</summary>
    private static (NodeUpdateInstaller Installer, Mock<IAgentGrpcDispatcher> Dispatcher, List<ActionConfig> Sent) Build(
        string? emitLine, string emitForAgent = Node, bool success = true, int exitCode = 0)
    {
        var dispatcher = new Mock<IAgentGrpcDispatcher>();
        var sent = new List<ActionConfig>();

        dispatcher
            .Setup(d => d.ExecuteRemoteCommandAsync(
                It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback<ActionConfig, PipelineExecutionContext, CancellationToken>((action, _, _) =>
            {
                sent.Add(action);
                if (emitLine is not null)
                    dispatcher.Raise(d => d.OutputReceived += null, emitForAgent, emitLine, "stdout");
            })
            .ReturnsAsync(new ActionResult(success, exitCode, success ? "" : "boom"));

        var installer = new NodeUpdateInstaller(dispatcher.Object, new Mock<IAppLogger>().Object);
        return (installer, dispatcher, sent);
    }

    [Fact]
    public async Task IsInstallSupportedAsync_Should_ReturnTrue_When_AgentReportsElevated()
    {
        var (installer, _, _) = Build(Line("""{"ok":true,"supported":true}"""));

        Assert.True(await installer.IsInstallSupportedAsync(Node, CancellationToken.None));
    }

    [Fact]
    public async Task IsInstallSupportedAsync_Should_ReturnFalse_When_AgentIsNotElevated()
    {
        // Setup-InteractiveAgent.ps1 nodes run as a logged-on user, so this is a real fleet state.
        var (installer, _, _) = Build(Line("""{"ok":true,"supported":false}"""));

        Assert.False(await installer.IsInstallSupportedAsync(Node, CancellationToken.None));
    }

    [Fact]
    public async Task SearchAsync_Should_ReturnTitles_When_AgentFindsUpdates()
    {
        var (installer, _, _) = Build(Line(
            """{"ok":true,"count":2,"titles":["2026-09 Cumulative Update (KB5000001)","Defender update"]}"""));

        var result = await installer.SearchAsync(Node, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(2, result.AvailableCount);
        Assert.Contains("KB5000001", result.Titles[0]);
    }

    [Fact]
    public async Task SearchAsync_Should_Fail_When_AgentProducesNoResultLine()
    {
        var (installer, _, _) = Build(emitLine: null);

        var result = await installer.SearchAsync(Node, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains(Node, result.Error);
    }

    [Fact]
    public async Task SearchAsync_Should_Fail_When_ResultLineBelongsToAnotherAgent()
    {
        // A pipeline logging on a different node must never be read as this node's answer.
        var (installer, _, _) = Build(Line("""{"ok":true,"count":9}"""), emitForAgent: "SOMEONE-ELSE");

        var result = await installer.SearchAsync(Node, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(0, result.AvailableCount);
    }

    [Fact]
    public async Task InstallAsync_Should_ReportRebootRequired_When_AgentSaysSo()
    {
        var (installer, _, _) = Build(Line(
            """{"ok":true,"installed":3,"failed":0,"rebootRequired":true}"""));

        var result = await installer.InstallAsync(Node, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(3, result.InstalledCount);
        Assert.True(result.RebootRequired);
    }

    [Fact]
    public async Task InstallAsync_Should_Fail_When_SomeUpdatesFailed()
    {
        // The process can exit 0 while individual updates failed; the payload is the authority, not the exit code.
        var (installer, _, _) = Build(Line(
            """{"ok":false,"installed":1,"failed":2,"rebootRequired":false,"error":"2 update(s) failed to install."}"""));

        var result = await installer.InstallAsync(Node, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("failed to install", result.Error);
    }

    [Fact]
    public async Task InstallAsync_Should_Fail_When_DispatcherThrows()
    {
        var dispatcher = new Mock<IAgentGrpcDispatcher>();
        dispatcher
            .Setup(d => d.ExecuteRemoteCommandAsync(
                It.IsAny<ActionConfig>(), It.IsAny<PipelineExecutionContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("channel is dead"));

        var installer = new NodeUpdateInstaller(dispatcher.Object, new Mock<IAppLogger>().Object);

        var result = await installer.InstallAsync(Node, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("channel is dead", result.Error);
    }

    [Fact]
    public async Task SearchAsync_Should_SendAnOnlineSearch_When_Dispatching()
    {
        var (installer, _, sent) = Build(Line("""{"ok":true,"count":0}"""));

        await installer.SearchAsync(Node, CancellationToken.None);

        var action = Assert.Single(sent);
        Assert.Equal(ActionType.RunRemoteCommand, action.Type);
        Assert.Equal(Node, action.AgentName);
        Assert.Contains("-EncodedCommand", action.Parameters);

        var encoded = action.Parameters[(action.Parameters.IndexOf("-EncodedCommand", StringComparison.Ordinal) + 16)..].Trim();
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));

        // The detector's cached search cannot see updates the node has not been offered yet.
        Assert.Contains("$searcher.Online = $true", script);
        Assert.Contains("IsInstalled=0 and IsHidden=0", script);
    }

    [Fact]
    public async Task InstallAsync_Should_AcceptEulaAndDownload_When_Dispatching()
    {
        var (installer, _, sent) = Build(Line("""{"ok":true,"installed":0,"failed":0}"""));

        await installer.InstallAsync(Node, CancellationToken.None);

        var encoded = sent[0].Parameters[(sent[0].Parameters.IndexOf("-EncodedCommand", StringComparison.Ordinal) + 16)..].Trim();
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));

        Assert.Contains("AcceptEula", script);
        Assert.Contains("CreateUpdateDownloader", script);
        Assert.Contains("CreateUpdateInstaller", script);
    }

    [Fact]
    public async Task Script_Should_NotUsePowerShellStreams_When_EmittingTheResult()
    {
        // Observed on WARMGR 2026-09-22: Write-Host and the progress stream are serialised into a CLIXML
        // blob on stderr by a redirected host, so the whole envelope was logged as [Error].
        var (installer, _, sent) = Build(Line("""{"ok":true,"count":0}"""));

        await installer.SearchAsync(Node, CancellationToken.None);

        var encoded = sent[0].Parameters[(sent[0].Parameters.IndexOf("-EncodedCommand", StringComparison.Ordinal) + 16)..].Trim();
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));

        Assert.DoesNotContain("Write-Host", script);
        Assert.Contains("[Console]::Out.WriteLine", script);
        Assert.Contains("$ProgressPreference = 'SilentlyContinue'", script);
    }

    [Fact]
    public async Task Script_Should_ExplainWindowsUpdateHResults_When_TheSearchFaults()
    {
        var (installer, _, sent) = Build(Line("""{"ok":true,"count":0}"""));

        await installer.SearchAsync(Node, CancellationToken.None);

        var encoded = sent[0].Parameters[(sent[0].Parameters.IndexOf("-EncodedCommand", StringComparison.Ordinal) + 16)..].Trim();
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));

        // A bare "Exception from HRESULT: 0x80244007" gives an operator nowhere to start.
        Assert.Contains("0x80244007", script);
        Assert.Contains("WU_E_PT_SOAPCLIENT_SOAPFAULT", script);
    }
}
