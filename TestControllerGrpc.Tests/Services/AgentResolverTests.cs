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
    // ExtractAgentNames � WatchItemConfig overload
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
    // ExtractAgentNames � EventConfig overload
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

    // ------------------------------------------------------------------
    // Ref / Template expansion
    //
    // A release-driven WatchList puts every action behind a <Ref>, so a resolver that walks only
    // Action and ActionGroup returns nothing, the caller skips TryLockAgents entirely, and an
    // in-flight pipeline holds ZERO agent locks. That is a safety hole, not a display glitch:
    // a second pipeline can dispatch to the same machines mid-run.
    // ------------------------------------------------------------------

    private static WatchItemConfig RefOnlyPipeline(params string[] templateIds) => new()
    {
        Tag = "SP2026 - Sanity 5 Nodes Smoke E2E",
        Events =
        [
            new EventConfig
            {
                Type = "Renamed",
                Children = [.. templateIds.Select(id => (IActionNode)new RefConfig { TemplateID = id })],
            },
        ],
    };

    private static TemplateConfig Template(string id, params string[] agentNames) => new()
    {
        ID = id,
        Children = [.. agentNames.Select(a => (IActionNode)new ActionConfig { AgentName = a, Command = "Prepare-Agent.bat" })],
    };

    [Fact]
    public void ExtractAgentNames_Should_ReturnAgents_When_EveryActionSitsBehindARef()
    {
        var wi = RefOnlyPipeline("PrepSanity");
        List<TemplateConfig> templates = [Template("PrepSanity", "jvgr1", "jvgr2", "jvhist", "jvkpri", "jvkbak")];

        var agents = AgentResolver.ExtractAgentNames(wi, null, templates);

        Assert.Equal(5, agents.Count);
        Assert.Contains("jvkpri", agents);
    }

    [Fact]
    public void ExtractAgentNames_Should_ReturnEmpty_When_TemplatesAreNotSupplied()
    {
        var wi = RefOnlyPipeline("PrepSanity");

        var agents = AgentResolver.ExtractAgentNames(wi, null);

        Assert.Empty(agents);
    }

    [Fact]
    public void ExtractAgentNames_Should_CollectAcrossTemplates_When_PipelineRefsSeveral()
    {
        var wi = RefOnlyPipeline("RevertSanity", "PrepSanity", "SmokeSanity");
        List<TemplateConfig> templates =
        [
            Template("RevertSanity", "jvgr1"),
            Template("PrepSanity", "jvgr2"),
            Template("SmokeSanity", "jvhist"),
        ];

        var agents = AgentResolver.ExtractAgentNames(wi, null, templates);

        Assert.Equal(3, agents.Count);
    }

    // ------------------------------------------------------------------
    // Resolver + lock manager seam.
    //
    // Each half was already covered alone, and both passed while the real hole was BETWEEN them:
    // the resolver returned nothing for a Ref-built pipeline, so the caller never reached
    // TryLockAgents and a second pipeline dispatched to machines the first was still using.
    // These drive the two together, the way a trigger does.
    // ------------------------------------------------------------------

    private static WatchItemConfig RefPipeline(string tag, string templateId) => new()
    {
        Tag = tag,
        Events = [new EventConfig { Type = "Renamed", Children = [new RefConfig { TemplateID = templateId }] }],
    };

    [Fact]
    public void TryLockAgents_Should_RefuseSecondPipeline_When_BothReachTheSameAgentsThroughRefTemplates()
    {
        string[] sanityNodes = ["jvgr1", "jvgr2", "jvhist", "jvkpri", "jvkbak"];
        List<TemplateConfig> templates = [Template("RevertSanity", sanityNodes)];
        var locks = new AgentLockManager();

        var first = RefPipeline("SP2023R2SP2 - Sanity 5 Nodes Smoke E2E", "RevertSanity");
        var firstAgents = AgentResolver.ExtractAgentNames(first, null, templates);
        var (firstLocked, _) = locks.TryLockAgents(firstAgents, "sess-A", first.Tag, "vinod", "WPF");

        var second = RefPipeline("SP2026R2 - Sanity 5 Nodes Smoke E2E", "RevertSanity");
        var secondAgents = AgentResolver.ExtractAgentNames(second, null, templates);
        var (secondLocked, conflicts) = locks.TryLockAgents(secondAgents, "sess-B", second.Tag, "vinod", "WPF");

        Assert.True(firstLocked);
        Assert.Equal(5, firstAgents.Count);

        // The whole point: a different pipeline, same machines, must not start.
        Assert.False(secondLocked);
        Assert.Equal(5, conflicts.Count);

        // The refusal has to say WHO holds them, or the operator cannot act on it.
        Assert.All(conflicts, c => Assert.Equal("sess-A", c.SessionId));
        Assert.All(conflicts, c => Assert.Equal(first.Tag, c.WatchItemTag));

        // Every lock still belongs to the first session - a refused attempt must steal nothing.
        Assert.All(sanityNodes, n => Assert.Equal("sess-A", locks.GetLock(n)!.SessionId));
    }

    [Fact]
    public void TryLockAgents_Should_AllowSecondPipeline_When_RefTemplatesUseDifferentAgents()
    {
        List<TemplateConfig> templates =
        [
            Template("SanityNodes", "jvgr1", "jvgr2"),
            Template("WarmNodes", "warmgr", "warmpri"),
        ];
        var locks = new AgentLockManager();

        var first = RefPipeline("SP2023R2SP2 - Sanity", "SanityNodes");
        locks.TryLockAgents(AgentResolver.ExtractAgentNames(first, null, templates), "sess-A", first.Tag, "vinod", "WPF");

        var second = RefPipeline("SP2023R2SP2 - WARM", "WarmNodes");
        var (secondLocked, conflicts) = locks.TryLockAgents(
            AgentResolver.ExtractAgentNames(second, null, templates), "sess-B", second.Tag, "vinod", "WPF");

        // Control for the test above: refusal must come from the shared agents, not from
        // "a second pipeline is running" - unrelated pipelines still run concurrently.
        Assert.True(secondLocked);
        Assert.Empty(conflicts);
    }

    [Fact]
    public void TryLockAgents_Should_RefuseSecondPipeline_When_AgentsAreTokensResolvedInsideRefTemplates()
    {
        List<TemplateConfig> templates = [Template("RevertSanity", "[_Agent1]", "[_Agent2]")];
        var parameters = new Dictionary<string, string> { ["_Agent1"] = "jvgr1", ["_Agent2"] = "jvgr2" };
        var locks = new AgentLockManager();

        var first = RefPipeline("SP2023R2SP2 - Sanity", "RevertSanity");
        var firstAgents = AgentResolver.ExtractAgentNames(first, parameters, templates);
        locks.TryLockAgents(firstAgents, "sess-A", first.Tag, "vinod", "WPF");

        // A different pipeline whose tokens resolve to the SAME machines must still be refused:
        // locks are keyed on the resolved hostname, not on the token text.
        var second = RefPipeline("SP2026R2 - Sanity", "RevertSanity");
        var secondAgents = AgentResolver.ExtractAgentNames(second, parameters, templates);
        var (secondLocked, conflicts) = locks.TryLockAgents(secondAgents, "sess-B", second.Tag, "vinod", "WPF");

        Assert.Equal(["jvgr1", "jvgr2"], firstAgents);
        Assert.False(secondLocked);
        Assert.Equal(2, conflicts.Count);
    }

    [Fact]
    public void ExtractAgentNames_Should_ResolveVariable_When_AgentNameIsATokenInsideATemplate()
    {
        var wi = RefOnlyPipeline("PrepSanity");
        List<TemplateConfig> templates = [Template("PrepSanity", "[_Agent1]")];
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["_Agent1"] = "jvgr1" };

        var agents = AgentResolver.ExtractAgentNames(wi, parameters, templates);

        Assert.Equal(["jvgr1"], agents);
    }

    [Fact]
    public void ExtractAgentNames_Should_DescendIntoGroups_When_TemplateWrapsItsActions()
    {
        var wi = RefOnlyPipeline("PrepSanity");
        List<TemplateConfig> templates =
        [
            new TemplateConfig
            {
                ID = "PrepSanity",
                Children =
                [
                    new ActionGroupConfig
                    {
                        Tag = "Prep",
                        Children = [new ActionConfig { AgentName = "jvgr1", Command = "x" }],
                    },
                ],
            },
        ];

        var agents = AgentResolver.ExtractAgentNames(wi, null, templates);

        Assert.Equal(["jvgr1"], agents);
    }

    [Fact]
    public void ExtractAgentNames_Should_FollowNestedRefs_When_ATemplateRefsAnother()
    {
        var wi = RefOnlyPipeline("Outer");
        List<TemplateConfig> templates =
        [
            new TemplateConfig { ID = "Outer", Children = [new RefConfig { TemplateID = "Inner" }] },
            Template("Inner", "jvkbak"),
        ];

        var agents = AgentResolver.ExtractAgentNames(wi, null, templates);

        Assert.Equal(["jvkbak"], agents);
    }

    [Fact]
    public void ExtractAgentNames_Should_Terminate_When_TemplatesReferenceEachOtherInACycle()
    {
        var wi = RefOnlyPipeline("A");
        List<TemplateConfig> templates =
        [
            new TemplateConfig
            {
                ID = "A",
                Children = [new ActionConfig { AgentName = "jvgr1", Command = "x" }, new RefConfig { TemplateID = "B" }],
            },
            new TemplateConfig { ID = "B", Children = [new RefConfig { TemplateID = "A" }] },
        ];

        var agents = AgentResolver.ExtractAgentNames(wi, null, templates);

        Assert.Equal(["jvgr1"], agents);
    }

    [Fact]
    public void ExtractAgentNames_Should_Ignore_When_RefNamesAnUnknownTemplate()
    {
        var wi = RefOnlyPipeline("DoesNotExist");
        List<TemplateConfig> templates = [Template("PrepSanity", "jvgr1")];

        var agents = AgentResolver.ExtractAgentNames(wi, null, templates);

        Assert.Empty(agents);
    }

    [Fact]
    public void ExtractAgentNames_TemplateOverload_Should_FollowRefs_When_TemplateRefsAnother()
    {
        var outer = new TemplateConfig { ID = "Outer", Children = [new RefConfig { TemplateID = "Inner" }] };
        List<TemplateConfig> templates = [outer, Template("Inner", "warmpri")];

        var agents = AgentResolver.ExtractAgentNames(outer, null, templates);

        Assert.Equal(["warmpri"], agents);
    }

    [Fact]
    public void ExtractAgentNames_GroupOverload_Should_FollowRefs_When_GroupContainsARef()
    {
        var group = new ActionGroupConfig { Tag = "Prep", Children = [new RefConfig { TemplateID = "PrepSanity" }] };
        List<TemplateConfig> templates = [Template("PrepSanity", "warmbak")];

        var agents = AgentResolver.ExtractAgentNames(group, null, templates);

        Assert.Equal(["warmbak"], agents);
    }
}
