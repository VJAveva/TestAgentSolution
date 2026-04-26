using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Tests for AgentResolver: extracting agent names from WatchItem
/// action trees with variable resolution.
/// </summary>
public class AgentResolverTests
{
    // ?????????????????????????????????????????????????????????????????
    // ExtractAgentNames — WatchItemConfig overload
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public void ExtractAgentNames_Should_ReturnEmpty_When_NoActions()
    {
        var wi = new WatchItemConfig
        {
            Tag = "Set1",
            Events = [new EventConfig { Type = "Renamed" }],
        };

        var agents = AgentResolver.ExtractAgentNames(wi);

        Assert.Empty(agents);
    }

    [Fact]
    public void ExtractAgentNames_Should_ReturnAgents_When_ActionsHaveAgentName()
    {
        var wi = new WatchItemConfig
        {
            Tag = "Set1",
            Events =
            [
                new EventConfig
                {
                    Type = "Renamed",
                    Children =
                    [
                        new ActionConfig { AgentName = "jvgr1", Command = "echo" },
                        new ActionConfig { AgentName = "jvkpri", Command = "echo" },
                    ],
                },
            ],
        };

        var agents = AgentResolver.ExtractAgentNames(wi);

        Assert.Equal(2, agents.Count);
        Assert.Contains("jvgr1", agents);
        Assert.Contains("jvkpri", agents);
    }

    [Fact]
    public void ExtractAgentNames_Should_Deduplicate_When_SameAgentMultipleTimes()
    {
        var wi = new WatchItemConfig
        {
            Tag = "Set1",
            Events =
            [
                new EventConfig
                {
                    Type = "Renamed",
                    Children =
                    [
                        new ActionConfig { AgentName = "jvgr1", Command = "cmd1" },
                        new ActionConfig { AgentName = "jvgr1", Command = "cmd2" },
                    ],
                },
            ],
        };

        var agents = AgentResolver.ExtractAgentNames(wi);

        Assert.Single(agents);
    }

    [Fact]
    public void ExtractAgentNames_Should_BeCaseInsensitive_When_Deduplicating()
    {
        var wi = new WatchItemConfig
        {
            Tag = "Set1",
            Events =
            [
                new EventConfig
                {
                    Type = "Renamed",
                    Children =
                    [
                        new ActionConfig { AgentName = "JVGR1", Command = "cmd1" },
                        new ActionConfig { AgentName = "jvgr1", Command = "cmd2" },
                    ],
                },
            ],
        };

        var agents = AgentResolver.ExtractAgentNames(wi);

        Assert.Single(agents);
    }

    [Fact]
    public void ExtractAgentNames_Should_ResolveVariable_When_ParametersProvided()
    {
        var wi = new WatchItemConfig
        {
            Tag = "Set1",
            Events =
            [
                new EventConfig
                {
                    Type = "Renamed",
                    Children =
                    [
                        new ActionConfig { AgentName = "[_AgentMachine]", Command = "echo" },
                    ],
                },
            ],
        };

        var parameters = new Dictionary<string, string>
        {
            ["_AgentMachine"] = "jvgr1",
        };

        var agents = AgentResolver.ExtractAgentNames(wi, parameters);

        Assert.Single(agents);
        Assert.Equal("jvgr1", agents[0]);
    }

    [Fact]
    public void ExtractAgentNames_Should_ResolveWithoutUnderscore_When_VariableHasUnderscore()
    {
        var wi = new WatchItemConfig
        {
            Tag = "Set1",
            Events =
            [
                new EventConfig
                {
                    Type = "Renamed",
                    Children =
                    [
                        new ActionConfig { AgentName = "[_AgentMachine]", Command = "echo" },
                    ],
                },
            ],
        };

        // Parameter stored without underscore prefix
        var parameters = new Dictionary<string, string>
        {
            ["AgentMachine"] = "jvkpri",
        };

        var agents = AgentResolver.ExtractAgentNames(wi, parameters);

        Assert.Single(agents);
        Assert.Equal("jvkpri", agents[0]);
    }

    [Fact]
    public void ExtractAgentNames_Should_KeepVariableLiteral_When_NotInParameters()
    {
        var wi = new WatchItemConfig
        {
            Tag = "Set1",
            Events =
            [
                new EventConfig
                {
                    Type = "Renamed",
                    Children =
                    [
                        new ActionConfig { AgentName = "[_UnknownVar]", Command = "echo" },
                    ],
                },
            ],
        };

        var agents = AgentResolver.ExtractAgentNames(wi, new Dictionary<string, string>());

        Assert.Single(agents);
        Assert.Equal("[_UnknownVar]", agents[0]);
    }

    [Fact]
    public void ExtractAgentNames_Should_SkipActions_When_NoAgentName()
    {
        var wi = new WatchItemConfig
        {
            Tag = "Set1",
            Events =
            [
                new EventConfig
                {
                    Type = "Renamed",
                    Children =
                    [
                        new ActionConfig { Command = "echo", Parameters = "hello" },
                        new ActionConfig { AgentName = "agent1", Command = "remote" },
                    ],
                },
            ],
        };

        var agents = AgentResolver.ExtractAgentNames(wi);

        Assert.Single(agents);
        Assert.Equal("agent1", agents[0]);
    }

    [Fact]
    public void ExtractAgentNames_Should_WalkNestedGroups_When_GroupsExist()
    {
        var wi = new WatchItemConfig
        {
            Tag = "Set1",
            Events =
            [
                new EventConfig
                {
                    Type = "Renamed",
                    Children =
                    [
                        new ActionGroupConfig
                        {
                            ExecutionType = ExecutionMode.Parallel,
                            Children =
                            [
                                new ActionConfig { AgentName = "agent1", Command = "a" },
                                new ActionGroupConfig
                                {
                                    Children =
                                    [
                                        new ActionConfig { AgentName = "agent2", Command = "b" },
                                    ],
                                },
                            ],
                        },
                    ],
                },
            ],
        };

        var agents = AgentResolver.ExtractAgentNames(wi);

        Assert.Equal(2, agents.Count);
        Assert.Contains("agent1", agents);
        Assert.Contains("agent2", agents);
    }

    [Fact]
    public void ExtractAgentNames_Should_CollectAcrossEvents_When_MultipleEvents()
    {
        var wi = new WatchItemConfig
        {
            Tag = "Set1",
            Events =
            [
                new EventConfig
                {
                    Type = "Renamed",
                    Children = [new ActionConfig { AgentName = "agent1", Command = "a" }],
                },
                new EventConfig
                {
                    Type = "Created",
                    Children = [new ActionConfig { AgentName = "agent2", Command = "b" }],
                },
            ],
        };

        var agents = AgentResolver.ExtractAgentNames(wi);

        Assert.Equal(2, agents.Count);
    }

    // ?????????????????????????????????????????????????????????????????
    // ExtractAgentNames — EventConfig overload
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public void ExtractAgentNames_EventOverload_Should_ReturnAgents_When_Present()
    {
        var ev = new EventConfig
        {
            Type = "Renamed",
            Children =
            [
                new ActionConfig { AgentName = "agent1", Command = "echo" },
            ],
        };

        var agents = AgentResolver.ExtractAgentNames(ev);

        Assert.Single(agents);
        Assert.Equal("agent1", agents[0]);
    }

    [Fact]
    public void ExtractAgentNames_Should_HandleNull_Parameters()
    {
        var wi = new WatchItemConfig
        {
            Tag = "Set1",
            Events =
            [
                new EventConfig
                {
                    Type = "Renamed",
                    Children =
                    [
                        new ActionConfig { AgentName = "[_Var]", Command = "echo" },
                    ],
                },
            ],
        };

        var agents = AgentResolver.ExtractAgentNames(wi, null);

        Assert.Single(agents);
        Assert.Equal("[_Var]", agents[0]);
    }
}
