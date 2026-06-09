using TestControllerGrpc.ViewModels.Execution;

namespace TestControllerGrpc.Tests.ViewModels;

/// <summary>
/// Comprehensive tests for ActionPillVM, AgentRowVM, and SessionCardVM logic —
/// covering derived properties, status transitions, dirty-checking, and edge cases.
/// </summary>
public class ExecutionViewModelDetailTests
{
    // ═══════════════════════════════════════════════════════════════════
    // ActionPillVM – DisplayLabel
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void DisplayLabel_Should_UseTag_When_TagIsSet()
    {
        var pill = new ActionPillVM { Tag = "Install Build" };
        Assert.Equal("Install Build", pill.DisplayLabel);
    }

    [Fact]
    public void DisplayLabel_Should_UseCommand_When_TagIsEmpty()
    {
        var pill = new ActionPillVM { Command = "echo hello" };
        Assert.Equal("echo hello", pill.DisplayLabel);
    }

    [Fact]
    public void DisplayLabel_Should_ExtractFileName_When_CommandIsPath()
    {
        var pill = new ActionPillVM { Command = @"\\server\scripts\install.cmd" };
        Assert.Equal("install", pill.DisplayLabel);
    }

    [Fact]
    public void DisplayLabel_Should_Truncate_When_LabelTooLong()
    {
        var pill = new ActionPillVM { Tag = "This is a very long action tag name that exceeds the limit" };
        Assert.True(pill.DisplayLabel.Length <= 22); // 20 + ".."
        Assert.EndsWith("..", pill.DisplayLabel);
    }

    [Fact]
    public void DisplayLabel_Should_ShowPercent_When_RunningWithProgress()
    {
        var pill = new ActionPillVM { Tag = "Install", Status = "Running", ProgressPercent = 42 };
        Assert.Contains("42%", pill.DisplayLabel);
    }

    [Fact]
    public void DisplayLabel_Should_ReturnAction_When_BothEmpty()
    {
        var pill = new ActionPillVM();
        Assert.Equal("Action", pill.DisplayLabel);
    }

    // ═══════════════════════════════════════════════════════════════════
    // ActionPillVM – Key
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Key_Should_UseTag_When_TagIsNonEmpty()
    {
        var pill = new ActionPillVM { Tag = "Install" };
        Assert.Equal("Install", pill.Key);
    }

    [Fact]
    public void Key_Should_UseFallback_When_TagIsEmpty()
    {
        var pill = new ActionPillVM { ActionType = "RunCommand", Command = "echo" };
        Assert.Equal("RunCommand|echo", pill.Key);
    }

    // ═══════════════════════════════════════════════════════════════════
    // ActionPillVM – StatusIcon
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Success", "\u2713")]
    [InlineData("Failed", "\u2717")]
    [InlineData("Running", "\u25CF")]
    [InlineData("Skipped", "\u2212")]
    [InlineData("Pending", "\u25CB")]
    [InlineData("Unknown", "\u25CB")]
    public void StatusIcon_Should_MapCorrectly_When_StatusSet(string status, string expected)
    {
        var pill = new ActionPillVM { Status = status };
        Assert.Equal(expected, pill.StatusIcon);
    }

    // ═══════════════════════════════════════════════════════════════════
    // ActionPillVM – Tooltip
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Tooltip_Should_IncludeCommand_When_CommandSet()
    {
        var pill = new ActionPillVM { Command = "dotnet test", Status = "Running" };
        Assert.Contains("Command: dotnet test", pill.Tooltip);
    }

    [Fact]
    public void Tooltip_Should_IncludeExitCode_When_NonZero()
    {
        var pill = new ActionPillVM { Status = "Failed", ExitCode = 1 };
        Assert.Contains("Exit code: 1", pill.Tooltip);
    }

    [Fact]
    public void Tooltip_Should_IncludeErrorMessage_When_Present()
    {
        var pill = new ActionPillVM { Status = "Failed", ErrorMessage = "Access denied" };
        Assert.Contains("Error: Access denied", pill.Tooltip);
    }

    [Fact]
    public void Tooltip_Should_NotIncludeExitCode_When_Zero()
    {
        var pill = new ActionPillVM { Status = "Success", ExitCode = 0 };
        Assert.DoesNotContain("Exit code", pill.Tooltip);
    }

    // ═══════════════════════════════════════════════════════════════════
    // AgentRowVM – UpdateAction
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void UpdateAction_Should_AddNewPill_When_NotExists()
    {
        var row = new AgentRowVM { AgentName = "Agent-01" };
        row.UpdateAction("Install", "RunRemoteCommand", "install.cmd", "Running");

        Assert.Single(row.Actions);
        Assert.Equal("Install", row.Actions[0].Tag);
        Assert.Equal("Running", row.Actions[0].Status);
    }

    [Fact]
    public void UpdateAction_Should_UpdateExisting_When_StatusChanges()
    {
        var row = new AgentRowVM { AgentName = "Agent-01" };
        row.UpdateAction("Install", "RunRemoteCommand", "install.cmd", "Running");
        row.UpdateAction("Install", "RunRemoteCommand", "install.cmd", "Success",
            exitCode: 0, duration: "30s");

        Assert.Single(row.Actions);
        Assert.Equal("Success", row.Actions[0].Status);
        Assert.Equal("30s", row.Actions[0].Duration);
    }

    [Fact]
    public void UpdateAction_Should_ChainMultiple_When_DifferentActions()
    {
        var row = new AgentRowVM { AgentName = "Agent-01" };
        row.UpdateAction("Install", "RunRemoteCommand", "install.cmd", "Success");
        row.UpdateAction("Test", "RunRemoteCommand", "test.cmd", "Running");
        row.UpdateAction("Email", "SendMail", "qa@co.com", "Pending");

        Assert.Equal(3, row.Actions.Count);
    }

    [Fact]
    public void UpdateAction_Should_NotDowngradeStatus_When_TerminalToRunning()
    {
        var row = new AgentRowVM { AgentName = "Agent-01" };
        row.UpdateAction("Install", "RunRemoteCommand", "install.cmd", "Success");
        row.UpdateAction("Install", "RunRemoteCommand", "install.cmd", "Running");

        Assert.Equal("Success", row.Actions[0].Status);
    }

    [Fact]
    public void UpdateAction_Should_NotDowngradeStatus_When_FailedToPending()
    {
        var row = new AgentRowVM { AgentName = "Agent-01" };
        row.UpdateAction("Install", "RunRemoteCommand", "install.cmd", "Failed");
        row.UpdateAction("Install", "RunRemoteCommand", "install.cmd", "Pending");

        Assert.Equal("Failed", row.Actions[0].Status);
    }

    [Fact]
    public void UpdateAction_Should_CalculateProgress_When_SomeComplete()
    {
        var row = new AgentRowVM { AgentName = "Agent-01" };
        row.UpdateAction("A1", "Run", "cmd1", "Success");
        row.UpdateAction("A2", "Run", "cmd2", "Running");
        row.UpdateAction("A3", "Run", "cmd3", "Pending");

        Assert.Equal(1, row.CompletedCount);
        Assert.Equal(3, row.TotalCount);
        Assert.Equal(33, row.ProgressPercent); // 1/3
    }

    [Fact]
    public void UpdateAction_Should_SetFailed_When_AnyActionFails()
    {
        var row = new AgentRowVM { AgentName = "Agent-01" };
        row.UpdateAction("A1", "Run", "cmd1", "Success");
        row.UpdateAction("A2", "Run", "cmd2", "Failed", exitCode: 1);

        Assert.Equal("Failed", row.Status);
    }

    [Fact]
    public void UpdateAction_Should_SetSuccess_When_AllComplete()
    {
        var row = new AgentRowVM { AgentName = "Agent-01" };
        row.UpdateAction("A1", "Run", "cmd1", "Success");
        row.UpdateAction("A2", "Run", "cmd2", "Success");

        Assert.Equal("Success", row.Status);
        Assert.Equal(100, row.ProgressPercent);
    }

    // ═══════════════════════════════════════════════════════════════════
    // AgentRowVM – StatusText
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Executing", "Executing")]
    [InlineData("Rebooting", "Rebooting...")]
    [InlineData("Success", "Done")]
    [InlineData("Failed", "Failed")]
    [InlineData("Idle", "Idle")]
    [InlineData("anything", "Idle")]
    public void StatusText_Should_MapCorrectly_When_StatusSet(string status, string expected)
    {
        var row = new AgentRowVM { Status = status };
        Assert.Equal(expected, row.StatusText);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SessionCardVM – StatusBadge
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Running", "Running")]
    [InlineData("Success", "Completed")]
    [InlineData("Failed", "Failed")]
    [InlineData("Cancelled", "Cancelled")]
    public void StatusBadge_Should_MapCorrectly_When_StatusSet(string status, string expected)
    {
        var card = new SessionCardVM { Status = status };
        Assert.Equal(expected, card.StatusBadge);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SessionCardVM – Summary
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Summary_Should_IncludeAllMetrics_When_DataPopulated()
    {
        var card = new SessionCardVM
        {
            PassedActions = 5,
            FailedActions = 2,
            ProgressPercent = 70,
        };
        card.Agents.Add(new AgentRowVM { AgentName = "A1" });
        card.Agents.Add(new AgentRowVM { AgentName = "A2" });

        Assert.Contains("2 agents", card.Summary);
        Assert.Contains("5 passed", card.Summary);
        Assert.Contains("2 failed", card.Summary);
        Assert.Contains("70%", card.Summary);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SessionCardVM – GetOrCreateAgent
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void GetOrCreateAgent_Should_CreateNew_When_NotExists()
    {
        var card = new SessionCardVM();
        var agent = card.GetOrCreateAgent("Agent-01");

        Assert.NotNull(agent);
        Assert.Equal("Agent-01", agent.AgentName);
        Assert.Single(card.Agents);
    }

    [Fact]
    public void GetOrCreateAgent_Should_ReturnExisting_When_AlreadyAdded()
    {
        var card = new SessionCardVM();
        var first = card.GetOrCreateAgent("Agent-01");
        var second = card.GetOrCreateAgent("Agent-01");

        Assert.Same(first, second);
        Assert.Single(card.Agents);
    }

    [Fact]
    public void GetOrCreateAgent_Should_BeCaseInsensitive_When_Matching()
    {
        var card = new SessionCardVM();
        var first = card.GetOrCreateAgent("Agent-01");
        var second = card.GetOrCreateAgent("agent-01");

        Assert.Same(first, second);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SessionCardVM – RecalculateCounters
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void RecalculateCounters_Should_SumAcrossAgents_When_MultipleAgents()
    {
        var card = new SessionCardVM();
        var a1 = card.GetOrCreateAgent("Agent-01");
        a1.UpdateAction("Install", "Run", "cmd", "Success");
        a1.UpdateAction("Test", "Run", "cmd2", "Failed");

        var a2 = card.GetOrCreateAgent("Agent-02");
        a2.UpdateAction("Install", "Run", "cmd", "Success");
        a2.UpdateAction("Test", "Run", "cmd2", "Success");

        card.RecalculateCounters();

        Assert.Equal(3, card.PassedActions);
        Assert.Equal(1, card.FailedActions);
        Assert.Equal(4, card.CompletedActions);
        Assert.Equal(100, card.ProgressPercent);
    }

    [Fact]
    public void RecalculateCounters_Should_HandleEmpty_When_NoAgents()
    {
        var card = new SessionCardVM();
        card.RecalculateCounters();

        Assert.Equal(0, card.PassedActions);
        Assert.Equal(0, card.FailedActions);
        Assert.Equal(0, card.ProgressPercent);
    }
}
