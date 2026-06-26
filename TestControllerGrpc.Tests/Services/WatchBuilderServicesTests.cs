using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests;

/// <summary>
/// Tests for the WatchItem Builder back-end services: severity-aware analysis
/// (<see cref="WatchListValidator.Analyze"/>), field suggestions
/// (<see cref="WatchFieldSuggestions"/>), and the silent migrator
/// (<see cref="WatchListMigrator"/>).
/// </summary>
public class WatchBuilderServicesTests
{
    // ── WatchListValidator.Analyze ──

    [Fact]
    public void Analyze_Should_ReturnNoErrors_When_ConfigIsValid()
    {
        var config = CreateValidConfig();

        var issues = WatchListValidator.Analyze(config);

        Assert.DoesNotContain(issues, i => i.Severity == WatchIssueSeverity.Error);
    }

    [Fact]
    public void Analyze_Should_SurfaceErrors_When_RequiredFieldMissing()
    {
        var config = new WatchListConfig
        {
            WatchItems = { new WatchItemConfig { Tag = "Test", Path = "", Events = { CreateEvent() } } }
        };

        var issues = WatchListValidator.Analyze(config);

        Assert.Contains(issues, i =>
            i.Severity == WatchIssueSeverity.Error && i.Message.Contains("Path is required"));
    }

    [Fact]
    public void Analyze_Should_WarnPlaintextPassword_When_PasswordSet()
    {
        var config = WithAction(new ActionConfig
        {
            Type = ActionType.RunRemoteCommand, Command = "cmd", AgentName = "Agent1", Password = "hunter2"
        });

        var issues = WatchListValidator.Analyze(config);

        Assert.Contains(issues, i =>
            i.Severity == WatchIssueSeverity.Warning && i.Message.Contains("plaintext"));
    }

    [Fact]
    public void Analyze_Should_WarnLiteralBuildNumber_When_FieldLooksLikeBuild()
    {
        var config = CreateValidConfig();
        config.WatchItems[0].BuildNumberField = "OAK_main_20260601.7";

        var issues = WatchListValidator.Analyze(config);

        Assert.Contains(issues, i =>
            i.Severity == WatchIssueSeverity.Warning && i.Message.Contains("literal build number"));
    }

    [Fact]
    public void Analyze_Should_WarnLiteralDropPath_When_FieldIsUncPath()
    {
        var config = CreateValidConfig();
        config.WatchItems[0].DropLocationField = @"\\server\share\drop";

        var issues = WatchListValidator.Analyze(config);

        Assert.Contains(issues, i =>
            i.Severity == WatchIssueSeverity.Warning && i.Message.Contains("literal path"));
    }

    [Fact]
    public void Analyze_Should_WarnEmptyGroup_When_ActionGroupHasNoChildren()
    {
        var config = new WatchListConfig
        {
            WatchItems =
            {
                new WatchItemConfig
                {
                    Tag = "Test", Path = @"C:\t",
                    Events = { new EventConfig
                    {
                        Type = "Renamed",
                        Children = { new ActionGroupConfig { Tag = "Group1" } }
                    }}
                }
            }
        };

        var issues = WatchListValidator.Analyze(config);

        Assert.Contains(issues, i =>
            i.Severity == WatchIssueSeverity.Warning && i.Message.Contains("ActionGroup is empty"));
    }

    [Fact]
    public void Analyze_Should_WarnPollExceedsTimeout_When_PollLongerThanWindow()
    {
        // PollInterval is ms, Timeout is seconds: 500000ms > 180s*1000.
        var config = WithAction(new ActionConfig
        {
            Type = ActionType.RunCommand, Command = "cmd", PollInterval = 500_000, Timeout = 180
        });

        var issues = WatchListValidator.Analyze(config);

        Assert.Contains(issues, i =>
            i.Severity == WatchIssueSeverity.Warning && i.Message.Contains("never fire"));
    }

    [Fact]
    public void Analyze_Should_NotWarnPoll_When_DefaultsUsed()
    {
        // Default PollInterval 1000ms vs Timeout 360s (=360000ms) must NOT warn.
        var config = WithAction(new ActionConfig
        {
            Type = ActionType.RunCommand, Command = "cmd", PollInterval = 1000, Timeout = 360
        });

        var issues = WatchListValidator.Analyze(config);

        Assert.DoesNotContain(issues, i => i.Message.Contains("never fire"));
    }

    [Fact]
    public void Analyze_Should_WarnSuspiciousTimeout_When_OverTwentyFourHours()
    {
        var config = WithAction(new ActionConfig
        {
            Type = ActionType.RunCommand, Command = "cmd", Timeout = 90_000   // >86400, <=172800
        });

        var issues = WatchListValidator.Analyze(config);

        Assert.Contains(issues, i =>
            i.Severity == WatchIssueSeverity.Warning && i.Message.Contains("over 24 hours"));
    }

    // ── WatchFieldSuggestions ──

    [Fact]
    public void Suggestions_Should_ExposeRealActionTypes()
    {
        var s = new WatchFieldSuggestions();

        Assert.Contains("RunRemoteCommand", s.ActionTypes);
        Assert.Contains("RunCommand", s.ActionTypes);
        Assert.Contains("SendMail", s.ActionTypes);
    }

    [Fact]
    public void Suggestions_Should_OfferAgentName_When_RemoteCommand()
    {
        var s = new WatchFieldSuggestions();

        Assert.Contains("AgentName", s.AttributesFor(ActionType.RunRemoteCommand));
        Assert.DoesNotContain("AgentName", s.AttributesFor(ActionType.SendMail));
    }

    [Fact]
    public void Suggestions_Should_OfferToAndFrom_When_SendMail()
    {
        var s = new WatchFieldSuggestions();

        var attrs = s.AttributesFor(ActionType.SendMail);
        Assert.Contains("To", attrs);
        Assert.Contains("From", attrs);
    }

    [Fact]
    public void Suggestions_Should_DefaultMailFrom_When_NewSendMail()
    {
        var s = new WatchFieldSuggestions();

        var action = s.NewActionDefaults(ActionType.SendMail);

        Assert.Equal(s.DefaultMailFrom, action.From);
        Assert.Equal(ActionType.SendMail, action.Type);
    }

    [Fact]
    public void Suggestions_Should_AcceptInjectedAgentNames()
    {
        var s = new WatchFieldSuggestions { AgentNames = new[] { "Agent1", "Agent2" } };

        Assert.Equal(2, s.AgentNames.Count);
        Assert.Contains("Agent1", s.AgentNames);
    }

    // ── WatchListMigrator ──

    [Fact]
    public void Upgrade_Should_NormalizeLiteralBuildNumberField()
    {
        var config = CreateValidConfig();
        config.WatchItems[0].BuildNumberField = "OAK_main_20260601.7";

        var (result, changed, _) = WatchListMigrator.Upgrade(config);

        Assert.True(changed);
        Assert.Equal("BuildNumber", result.WatchItems[0].BuildNumberField);
    }

    [Fact]
    public void Upgrade_Should_NormalizeLiteralDropLocationField()
    {
        var config = CreateValidConfig();
        config.WatchItems[0].DropLocationField = @"\\server\share\drop";

        var (result, changed, _) = WatchListMigrator.Upgrade(config);

        Assert.True(changed);
        Assert.Equal("DropLocation", result.WatchItems[0].DropLocationField);
    }

    [Fact]
    public void Upgrade_Should_RemoveEmptyActionGroups()
    {
        var config = new WatchListConfig
        {
            WatchItems =
            {
                new WatchItemConfig
                {
                    Tag = "Test", Path = @"C:\t",
                    Events = { new EventConfig
                    {
                        Type = "Renamed",
                        Children =
                        {
                            new ActionConfig { Type = ActionType.RunCommand, Command = "echo" },
                            new ActionGroupConfig { Tag = "EmptyGroup" }
                        }
                    }}
                }
            }
        };

        var (result, changed, _) = WatchListMigrator.Upgrade(config);

        Assert.True(changed);
        Assert.Single(result.WatchItems[0].Events[0].Children);
        Assert.IsType<ActionConfig>(result.WatchItems[0].Events[0].Children[0]);
    }

    [Fact]
    public void Upgrade_Should_BeIdempotent()
    {
        var config = CreateValidConfig();
        config.WatchItems[0].BuildNumberField = "OAK_main_20260601.7";

        WatchListMigrator.Upgrade(config);
        var (_, changedSecond, _) = WatchListMigrator.Upgrade(config);

        Assert.False(changedSecond);
    }

    [Fact]
    public void Upgrade_Should_ReportNoChange_When_ConfigIsCanonical()
    {
        var config = CreateValidConfig();

        var (_, changed, notes) = WatchListMigrator.Upgrade(config);

        Assert.False(changed);
        Assert.Empty(notes);
    }

    // ── Helpers ──

    private static WatchListConfig CreateValidConfig() => new()
    {
        WatchItems =
        {
            new WatchItemConfig { Tag = "TestBuild", Path = @"C:\Trigger", Events = { CreateEvent() } }
        }
    };

    private static EventConfig CreateEvent() => new()
    {
        Type = "Renamed",
        Children = { new ActionConfig { Type = ActionType.RunCommand, Command = "echo hello" } }
    };

    private static WatchListConfig WithAction(ActionConfig action) => new()
    {
        WatchItems =
        {
            new WatchItemConfig
            {
                Tag = "Test", Path = @"C:\t",
                Events = { new EventConfig { Type = "Renamed", Children = { action } } }
            }
        }
    };
}
