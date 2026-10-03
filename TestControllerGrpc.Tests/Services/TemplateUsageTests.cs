using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// A Template carries no Initialize, so running one from the library leaves every [Token]
/// unresolved. The only way it can resolve anything is through a pipeline that Refs it, so the
/// set of such pipelines is what the UI offers - and an empty set means the node cannot be run.
/// </summary>
public class TemplateUsageTests
{
    private static RefConfig Ref(string id) => new() { TemplateID = id };

    private static WatchItemConfig Pipeline(string tag, params IActionNode[] children) => new()
    {
        Tag = tag,
        Path = @"C:\Triggers",
        Events = [new EventConfig { Type = "Renamed", Children = [.. children] }],
    };

    private static TemplateConfig Template(string id, params IActionNode[] children) => new()
    {
        ID = id,
        Children = [.. children],
    };

    [Fact]
    public void PipelinesReferencing_Should_ReturnThePipeline_When_ItRefsTheTemplateDirectly()
    {
        var config = new WatchListConfig
        {
            WatchItems = [Pipeline("Sanity 5 Nodes", Ref("PrepSanity"))],
            Templates = [Template("PrepSanity")],
        };

        Assert.Equal(["Sanity 5 Nodes"], TemplateUsage.PipelinesReferencing(config, "PrepSanity"));
    }

    [Fact]
    public void PipelinesReferencing_Should_FindIt_When_TheRefIsNestedInGroups()
    {
        var config = new WatchListConfig
        {
            WatchItems =
            [
                Pipeline("Warm 4 Nodes",
                    new ActionGroupConfig
                    {
                        Tag = "Outer",
                        Children = [new ActionGroupConfig { Tag = "Inner", Children = [Ref("PrepWarm")] }],
                    }),
            ],
            Templates = [Template("PrepWarm")],
        };

        Assert.Equal(["Warm 4 Nodes"], TemplateUsage.PipelinesReferencing(config, "PrepWarm"));
    }

    /// <summary>A template may Ref another template, so usage is transitive.</summary>
    [Fact]
    public void PipelinesReferencing_Should_FollowRefs_When_ATemplateRefsAnother()
    {
        var config = new WatchListConfig
        {
            WatchItems = [Pipeline("Sanity 5 Nodes", Ref("Outer"))],
            Templates = [Template("Outer", Ref("Inner")), Template("Inner")],
        };

        Assert.Equal(["Sanity 5 Nodes"], TemplateUsage.PipelinesReferencing(config, "Inner"));
    }

    [Fact]
    public void PipelinesReferencing_Should_ListEveryPipeline_When_TheTemplateIsShared()
    {
        var config = new WatchListConfig
        {
            WatchItems =
            [
                Pipeline("Sanity 5 Nodes", Ref("Shared")),
                Pipeline("Warm 4 Nodes", Ref("Shared")),
                Pipeline("Unrelated", Ref("Other")),
            ],
            Templates = [Template("Shared"), Template("Other")],
        };

        Assert.Equal(["Sanity 5 Nodes", "Warm 4 Nodes"], TemplateUsage.PipelinesReferencing(config, "Shared"));
    }

    /// <summary>
    /// The visited set is a cycle guard, not a global memo. Sharing it across pipelines would make
    /// the second one skip the template and silently drop a valid choice from the dialog.
    /// </summary>
    [Fact]
    public void PipelinesReferencing_Should_NotSkipLaterPipelines_When_TheyShareAnIntermediateTemplate()
    {
        var config = new WatchListConfig
        {
            WatchItems =
            [
                Pipeline("First", Ref("Middle")),
                Pipeline("Second", Ref("Middle")),
            ],
            Templates = [Template("Middle", Ref("Leaf")), Template("Leaf")],
        };

        Assert.Equal(["First", "Second"], TemplateUsage.PipelinesReferencing(config, "Leaf"));
    }

    [Fact]
    public void PipelinesReferencing_Should_ReturnEmpty_When_NothingRefsTheTemplate()
    {
        var config = new WatchListConfig
        {
            WatchItems = [Pipeline("Sanity 5 Nodes", Ref("PrepSanity"))],
            Templates = [Template("PrepSanity"), Template("Orphan")],
        };

        Assert.Empty(TemplateUsage.PipelinesReferencing(config, "Orphan"));
    }

    [Fact]
    public void PipelinesReferencing_Should_Terminate_When_TemplatesFormACycle()
    {
        var config = new WatchListConfig
        {
            WatchItems = [Pipeline("Looping", Ref("A"))],
            Templates = [Template("A", Ref("B")), Template("B", Ref("A"))],
        };

        Assert.Equal(["Looping"], TemplateUsage.PipelinesReferencing(config, "B"));
        Assert.Empty(TemplateUsage.PipelinesReferencing(config, "Missing"));
    }

    [Fact]
    public void PipelinesReferencing_Should_MatchCaseInsensitively_When_IdCasingDiffers()
    {
        var config = new WatchListConfig
        {
            WatchItems = [Pipeline("Sanity 5 Nodes", Ref("prepsanity"))],
            Templates = [Template("PrepSanity")],
        };

        Assert.Equal(["Sanity 5 Nodes"], TemplateUsage.PipelinesReferencing(config, "PrepSanity"));
    }

    [Fact]
    public void PipelinesReferencing_Should_ReturnEmpty_When_TemplateIdIsBlank()
    {
        var config = new WatchListConfig
        {
            WatchItems = [Pipeline("Sanity 5 Nodes", Ref("PrepSanity"))],
            Templates = [Template("PrepSanity")],
        };

        Assert.Empty(TemplateUsage.PipelinesReferencing(config, ""));
        Assert.Empty(TemplateUsage.PipelinesReferencing(config, null));
    }

    /// <summary>Duplicate template IDs are a validator error; this must not throw over them.</summary>
    [Fact]
    public void PipelinesReferencing_Should_NotThrow_When_TemplateIdsAreDuplicated()
    {
        var config = new WatchListConfig
        {
            WatchItems = [Pipeline("Sanity 5 Nodes", Ref("Dup"))],
            Templates = [Template("Dup"), Template("Dup")],
        };

        Assert.Equal(["Sanity 5 Nodes"], TemplateUsage.PipelinesReferencing(config, "Dup"));
    }

    [Fact]
    public void PipelinesReferencing_Should_SkipPipelines_When_TagIsBlank()
    {
        var config = new WatchListConfig
        {
            WatchItems = [Pipeline("", Ref("PrepSanity")), Pipeline("Real", Ref("PrepSanity"))],
            Templates = [Template("PrepSanity")],
        };

        Assert.Equal(["Real"], TemplateUsage.PipelinesReferencing(config, "PrepSanity"));
    }
}
