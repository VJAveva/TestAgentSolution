using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Smart-editing rules for the WatchList editor: the agent roster check, the apply-to-agents
/// fan-out, and the new-action defaults.
/// </summary>
public sealed class SmartEditTests
{
    private static WatchListConfig ConfigWithRemoteAction(string agentName, string tag = "Install")
    {
        var action = new ActionConfig
        {
            Type = ActionType.RunRemoteCommand,
            AgentName = agentName,
            Command = "cmd",
            Parameters = "/c install.bat",
            Tag = tag,
            Timeout = 600,
        };
        var group = new ActionGroupConfig { Tag = "G", ExecutionType = ExecutionMode.Sequential };
        group.Children.Add(action);
        var ev = new EventConfig { Type = "Created", ExecutionType = ExecutionMode.Sequential };
        ev.Children.Add(group);
        var wi = new WatchItemConfig { Tag = "WI", Path = @"C:\drops", Filter = "*.trigger" };
        wi.Events.Add(ev);

        var config = new WatchListConfig();
        config.WatchItems.Add(wi);
        return config;
    }

    // ── unknown agent ──

    [Fact]
    public void Analyze_Should_FlagAgent_When_NotInKnownRoster()
    {
        var config = ConfigWithRemoteAction("GHOSTNODE");

        var issues = WatchListValidator.Analyze(config, new[] { "JVGR1", "JVGR2" });

        var issue = Assert.Single(issues, i => i.Message.Contains("GHOSTNODE"));
        Assert.Equal(WatchIssueSeverity.Error, issue.Severity);
    }

    [Fact]
    public void Analyze_Should_NotFlagAgent_When_InKnownRoster()
    {
        var config = ConfigWithRemoteAction("JVGR1");

        var issues = WatchListValidator.Analyze(config, new[] { "JVGR1", "JVGR2" });

        Assert.DoesNotContain(issues, i => i.Message.Contains("not a registered agent"));
    }

    /// <summary>
    /// A tokenised agent resolves from the parameter file at run time. Flagging it would light up
    /// every templated pipeline, so the rule must skip it.
    /// </summary>
    [Theory]
    [InlineData("[_Agent1]")]
    [InlineData("[Agent1]")]
    public void Analyze_Should_NotFlagAgent_When_NameIsAToken(string agentName)
    {
        var config = ConfigWithRemoteAction(agentName);

        var issues = WatchListValidator.Analyze(config, new[] { "JVGR1" });

        Assert.DoesNotContain(issues, i => i.Message.Contains("not a registered agent"));
    }

    /// <summary>No roster supplied means "cannot judge", not "everything is wrong".</summary>
    [Fact]
    public void Analyze_Should_NotFlagAgent_When_RosterIsNotSupplied()
    {
        var config = ConfigWithRemoteAction("GHOSTNODE");

        var issues = WatchListValidator.Analyze(config);

        Assert.DoesNotContain(issues, i => i.Message.Contains("not a registered agent"));
    }

    // ── apply to agents ──

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(9)]
    public void BuildAgentFanOutGroup_Should_CreateOneCopyPerAgent_When_AgentsSelected(int count)
    {
        var agents = Enumerable.Range(1, count).Select(i => $"AGENT{i}").ToList();
        var action = new ActionConfig
        {
            Type = ActionType.RunCommand,
            Command = "cmd",
            Parameters = "/c install.bat [_BuildNumber]",
            Tag = "Install",
            Timeout = 3600,
        };

        var group = MainViewModel.BuildAgentFanOutGroup(action, agents);

        Assert.Equal(ExecutionMode.Parallel, group.ExecutionType);
        Assert.Equal(count, group.Children.Count);
        Assert.Equal(agents, group.Children.Cast<ActionConfig>().Select(a => a.AgentName));
        Assert.All(group.Children.Cast<ActionConfig>(), a =>
        {
            Assert.Equal(ActionType.RunRemoteCommand, a.Type);
            Assert.Equal(action.Parameters, a.Parameters);
            Assert.Equal(action.Timeout, a.Timeout);
        });
    }

    /// <summary>
    /// NodeId matches progress events to tree nodes within a run, so shared ids would make every
    /// copy report against the same node.
    /// </summary>
    [Fact]
    public void BuildAgentFanOutGroup_Should_GiveEachCopyItsOwnNodeId_When_Cloning()
    {
        var action = new ActionConfig { Type = ActionType.RunCommand, Command = "cmd", Tag = "T" };

        var group = MainViewModel.BuildAgentFanOutGroup(action, new[] { "A", "B", "C" });

        var ids = group.Children.Cast<ActionConfig>().Select(a => a.NodeId).ToList();
        Assert.Equal(3, ids.Distinct().Count());
        Assert.DoesNotContain(action.NodeId, ids);
    }

    // ── new action defaults ──

    [Theory]
    [InlineData(ActionType.RunCommand)]
    [InlineData(ActionType.RunRemoteCommand)]
    public void NewActionDefaults_Should_KeepPollIntervalBelowTimeout_When_CommandAction(ActionType type)
    {
        var action = new WatchFieldSuggestions().NewActionDefaults(type);

        // Timeout is SECONDS, PollInterval is MILLISECONDS - the comparison must convert.
        Assert.True(action.PollInterval < action.Timeout * 1000L,
            $"PollInterval {action.PollInterval}ms must be under Timeout {action.Timeout}s.");
        Assert.Equal(WatchFieldSuggestions.DefaultTimeoutSeconds, action.Timeout);
        Assert.Equal(WatchFieldSuggestions.DefaultPollIntervalMs, action.PollInterval);
    }

    /// <summary>The defaults must not themselves trip the validator's poll-vs-timeout warning.</summary>
    [Theory]
    [InlineData(ActionType.RunCommand)]
    [InlineData(ActionType.RunRemoteCommand)]
    public void NewActionDefaults_Should_NotRaiseAnyIssue_When_Analyzed(ActionType type)
    {
        var action = new WatchFieldSuggestions().NewActionDefaults(type);
        action.AgentName = "JVGR1";
        action.Tag = "Step";

        var group = new ActionGroupConfig { Tag = "G", ExecutionType = ExecutionMode.Sequential };
        group.Children.Add(action);
        var ev = new EventConfig { Type = "Created", ExecutionType = ExecutionMode.Sequential };
        ev.Children.Add(group);
        var wi = new WatchItemConfig { Tag = "WI", Path = @"C:\drops", Filter = "*.trigger" };
        wi.Events.Add(ev);
        var config = new WatchListConfig();
        config.WatchItems.Add(wi);

        var issues = WatchListValidator.Analyze(config, new[] { "JVGR1" });

        Assert.DoesNotContain(issues, i => i.Message.Contains("PollInterval"));
    }

    [Theory]
    [InlineData("Install SP2023", "cmd", "/c echo hi")]
    [InlineData("Step 1", "install.bat", "")]
    [InlineData("Step 1", "cmd", "/c C:\\Setup\\Install-Build.bat")]
    public void ApplyInstallTimeoutIfDetected_Should_RaiseTimeout_When_InstallMentionedAnywhere(
        string tag, string command, string parameters)
    {
        var action = new WatchFieldSuggestions().NewActionDefaults(ActionType.RunRemoteCommand);
        action.Tag = tag;
        action.Command = command;
        action.Parameters = parameters;

        WatchFieldSuggestions.ApplyInstallTimeoutIfDetected(action);

        Assert.Equal(WatchFieldSuggestions.InstallTimeoutSeconds, action.Timeout);
        Assert.True(action.PollInterval < action.Timeout * 1000L);
    }

    [Fact]
    public void ApplyInstallTimeoutIfDetected_Should_LeaveTimeout_When_NotAnInstall()
    {
        var action = new WatchFieldSuggestions().NewActionDefaults(ActionType.RunCommand);
        action.Tag = "Copy files";
        action.Command = "cmd";
        action.Parameters = "/c xcopy a b";

        WatchFieldSuggestions.ApplyInstallTimeoutIfDetected(action);

        Assert.Equal(WatchFieldSuggestions.DefaultTimeoutSeconds, action.Timeout);
    }

    /// <summary>A hand-tuned timeout must survive - the helper only replaces the untouched default.</summary>
    [Fact]
    public void ApplyInstallTimeoutIfDetected_Should_PreserveTimeout_When_OperatorAlreadySetIt()
    {
        var action = new WatchFieldSuggestions().NewActionDefaults(ActionType.RunCommand);
        action.Tag = "Install SP";
        action.Timeout = 7200;

        WatchFieldSuggestions.ApplyInstallTimeoutIfDetected(action);

        Assert.Equal(7200, action.Timeout);
    }
}
