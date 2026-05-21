using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests;

/// <summary>
/// CORE-002: Tests for <see cref="WatchListValidator"/> that validates
/// parsed WatchListConfig before execution.
/// </summary>
public class WatchListValidatorTests
{
    [Fact]
    public void Validate_ValidConfig_ReturnsNoErrors()
    {
        var config = CreateValidConfig();

        var errors = WatchListValidator.Validate(config);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_EmptyWatchItemTag_ReturnsError()
    {
        var config = new WatchListConfig
        {
            WatchItems =
            {
                new WatchItemConfig { Tag = "", Path = @"C:\test", Events = { CreateEvent() } }
            }
        };

        var errors = WatchListValidator.Validate(config);

        Assert.Single(errors);
        Assert.Contains("empty Tag", errors[0].Message);
    }

    [Fact]
    public void Validate_DuplicateWatchItemTag_ReturnsError()
    {
        var config = new WatchListConfig
        {
            WatchItems =
            {
                new WatchItemConfig { Tag = "Build1", Path = @"C:\a", Events = { CreateEvent() } },
                new WatchItemConfig { Tag = "Build1", Path = @"C:\b", Events = { CreateEvent() } },
            }
        };

        var errors = WatchListValidator.Validate(config);

        Assert.Single(errors);
        Assert.Contains("Duplicate WatchItem tag", errors[0].Message);
    }

    [Fact]
    public void Validate_EmptyPath_ReturnsError()
    {
        var config = new WatchListConfig
        {
            WatchItems =
            {
                new WatchItemConfig { Tag = "Test", Path = "", Events = { CreateEvent() } }
            }
        };

        var errors = WatchListValidator.Validate(config);

        Assert.Single(errors);
        Assert.Contains("Path is required", errors[0].Message);
    }

    [Fact]
    public void Validate_NoEvents_ReturnsError()
    {
        var config = new WatchListConfig
        {
            WatchItems = { new WatchItemConfig { Tag = "Test", Path = @"C:\t" } }
        };

        var errors = WatchListValidator.Validate(config);

        Assert.Single(errors);
        Assert.Contains("no events", errors[0].Message);
    }

    [Fact]
    public void Validate_RunRemoteCommand_WithoutAgent_ReturnsError()
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
                        Children = { new ActionConfig { Type = ActionType.RunRemoteCommand, Command = "cmd /c echo hi", AgentName = "" } }
                    }}
                }
            }
        };

        var errors = WatchListValidator.Validate(config);

        Assert.Single(errors);
        Assert.Contains("RunRemoteCommand requires AgentName", errors[0].Message);
    }

    [Fact]
    public void Validate_RunCommand_WithoutCommand_ReturnsError()
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
                        Children = { new ActionConfig { Type = ActionType.RunCommand, Command = "" } }
                    }}
                }
            }
        };

        var errors = WatchListValidator.Validate(config);

        Assert.Single(errors);
        Assert.Contains("Command is required", errors[0].Message);
    }

    [Fact]
    public void Validate_NegativeTimeout_ReturnsError()
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
                        Children = { new ActionConfig { Type = ActionType.RunCommand, Command = "echo", Timeout = -5 } }
                    }}
                }
            }
        };

        var errors = WatchListValidator.Validate(config);

        Assert.Single(errors);
        Assert.Contains("negative", errors[0].Message);
    }

    [Fact]
    public void Validate_ExcessiveTimeout_ReturnsError()
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
                        Children = { new ActionConfig { Type = ActionType.RunCommand, Command = "echo", Timeout = 999_999 } }
                    }}
                }
            }
        };

        var errors = WatchListValidator.Validate(config);

        Assert.Single(errors);
        Assert.Contains("exceeds maximum", errors[0].Message);
    }

    [Fact]
    public void Validate_UnresolvedTemplateRef_ReturnsError()
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
                        Children = { new RefConfig { TemplateID = "NonExistent" } }
                    }}
                }
            }
        };

        var errors = WatchListValidator.Validate(config);

        Assert.Single(errors);
        Assert.Contains("unknown template", errors[0].Message);
    }

    [Fact]
    public void Validate_ValidTemplateRef_NoError()
    {
        var config = new WatchListConfig
        {
            Templates = { new TemplateConfig { ID = "MyTemplate", Children = { new ActionConfig { Type = ActionType.RunCommand, Command = "echo" } } } },
            WatchItems =
            {
                new WatchItemConfig
                {
                    Tag = "Test", Path = @"C:\t",
                    Events = { new EventConfig
                    {
                        Type = "Renamed",
                        Children = { new RefConfig { TemplateID = "MyTemplate" } }
                    }}
                }
            }
        };

        var errors = WatchListValidator.Validate(config);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_SendMail_WithoutTo_ReturnsError()
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
                        Children = { new ActionConfig { Type = ActionType.SendMail, To = "" } }
                    }}
                }
            }
        };

        var errors = WatchListValidator.Validate(config);

        Assert.Single(errors);
        Assert.Contains("SendMail requires To", errors[0].Message);
    }

    [Fact]
    public void Validate_NegativeMaxRetries_ReturnsError()
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
                        Children = { new ActionConfig { Type = ActionType.RunCommand, Command = "echo", MaxRetries = -1 } }
                    }}
                }
            }
        };

        var errors = WatchListValidator.Validate(config);

        Assert.Single(errors);
        Assert.Contains("MaxRetries cannot be negative", errors[0].Message);
    }

    // ── Helpers ──

    private static WatchListConfig CreateValidConfig() => new()
    {
        WatchItems =
        {
            new WatchItemConfig
            {
                Tag = "TestBuild", Path = @"C:\Trigger",
                Events = { CreateEvent() }
            }
        }
    };

    private static EventConfig CreateEvent() => new()
    {
        Type = "Renamed",
        Children = { new ActionConfig { Type = ActionType.RunCommand, Command = "echo hello" } }
    };
}
