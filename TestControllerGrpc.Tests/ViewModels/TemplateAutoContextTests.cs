using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Tests.ViewModels;

/// <summary>
/// A template has no settings of its own, so the Library previewed raw [_Tokens] until someone
/// answered a modal - even when the answer was obvious because a pipeline using the template was
/// running, or because only one pipeline Refs it at all.
/// </summary>
[Collection("TokenState")]
public class TemplateAutoContextTests : IDisposable
{
    public TemplateAutoContextTests()
    {
        TreeNodeViewModel.ClearTokenScopes();
        TreeNodeViewModel.ClearSessionValues();
        TemplateRunContext.ClearAll();
    }

    public void Dispose()
    {
        TreeNodeViewModel.ClearTokenScopes();
        TreeNodeViewModel.ClearSessionValues();
        TemplateRunContext.ClearAll();
        GC.SuppressFinalize(this);
    }

    private static Func<string, bool> Running(params string[] tags)
        => tag => tags.Contains(tag, StringComparer.OrdinalIgnoreCase);

    private static Func<string, bool> NothingRunning() => _ => false;

    // ── the decision ────────────────────────────────────────────────────────

    [Fact]
    public void Decide_Should_PickTheRunningPipeline_When_OneOfTheCandidatesIsExecuting()
    {
        var (pipeline, source) = TemplateContextResolver.Decide(
            ["SP2026", "SP2023R2SP2"], Running("SP2023R2SP2"));

        Assert.Equal("SP2023R2SP2", pipeline);
        Assert.Equal(TemplateContextSource.Running, source);
    }

    [Fact]
    public void Decide_Should_PickTheOnlyCandidate_When_NothingIsRunning()
    {
        var (pipeline, source) = TemplateContextResolver.Decide(["SP2026"], NothingRunning());

        Assert.Equal("SP2026", pipeline);
        Assert.Equal(TemplateContextSource.Auto, source);
    }

    [Fact]
    public void Decide_Should_Ask_When_SeveralPipelinesQualifyAndNoneIsRunning()
    {
        var (pipeline, _) = TemplateContextResolver.Decide(
            ["SP2026", "SP2023R2SP2"], NothingRunning());

        Assert.Null(pipeline);
    }

    [Fact]
    public void Decide_Should_PreferRunning_Over_TheSingleCandidateRule()
    {
        var (_, source) = TemplateContextResolver.Decide(["SP2026"], Running("SP2026"));

        Assert.Equal(TemplateContextSource.Running, source);
    }

    [Fact]
    public void Decide_Should_ReturnNothing_When_NoPipelineRefsTheTemplate()
    {
        var (pipeline, _) = TemplateContextResolver.Decide([], Running("SP2026"));

        Assert.Null(pipeline);
    }

    // ── the chip ────────────────────────────────────────────────────────────

    private static TreeNodeViewModel TemplateNode(string id = "InstallSanity_Single")
        => TreeNodeViewModel.FromTemplateList([new TemplateConfig { ID = id }]).Children[0];

    [Fact]
    public void Chip_Should_SayRunning_When_TheContextCameFromALiveRun()
    {
        TemplateRunContext.Set("InstallSanity_Single", "SP2023R2SP2", TemplateContextSource.Running);

        Assert.Equal("Context: SP2023R2SP2 (running)", TemplateNode().TemplateContextLabel);
    }

    [Fact]
    public void Chip_Should_SayAuto_When_OnlyOnePipelineRefsTheTemplate()
    {
        TemplateRunContext.Set("InstallSanity_Single", "SP2026", TemplateContextSource.Auto);

        Assert.Equal("Context: SP2026 (auto)", TemplateNode().TemplateContextLabel);
    }

    [Fact]
    public void Chip_Should_CarryNoSuffix_When_TheUserChoseIt()
    {
        TemplateRunContext.Set("InstallSanity_Single", "SP2026");

        Assert.Equal("Context: SP2026", TemplateNode().TemplateContextLabel);
    }

    [Fact]
    public void Chip_Should_BeHidden_When_NoContextIsSet()
    {
        var node = TemplateNode();

        Assert.False(node.HasTemplateContext);
        Assert.Equal("", node.TemplateContextLabel);
        Assert.Equal("", node.TemplateContextSuffix);
    }

    /// <summary>
    /// The chip is width-constrained and trims with an ellipsis. Rendering it as one string ate the
    /// suffix - "Context: SP2023R2SP2 - Sanit..." - which is the only part that says WHY.
    /// </summary>
    [Fact]
    public void Chip_Should_SplitPipelineFromProvenance_So_EllipsisCannotEatTheSuffix()
    {
        TemplateRunContext.Set("InstallSanity_Single",
            "SP2023R2SP2 - Sanity 5 Nodes Smoke E2E", TemplateContextSource.Running);

        var node = TemplateNode();

        Assert.Equal("Context: SP2023R2SP2 - Sanity 5 Nodes Smoke E2E", node.TemplateContextPipeline);
        Assert.Equal(" (running)", node.TemplateContextSuffix);
        Assert.Equal(node.TemplateContextPipeline + node.TemplateContextSuffix, node.TemplateContextLabel);
    }

    [Fact]
    public void Chip_Should_CarryAnEmptySuffix_When_TheUserChoseTheContext()
    {
        TemplateRunContext.Set("InstallSanity_Single", "SP2026");

        var node = TemplateNode();

        Assert.Equal("Context: SP2026", node.TemplateContextPipeline);
        Assert.Equal("", node.TemplateContextSuffix);
    }

    [Fact]
    public void Context_Should_ReportItsSource_So_TheChipCanDistinguishAnAutoPickFromAnAnswer()
    {
        TemplateRunContext.Set("T1", "P1", TemplateContextSource.Auto);
        TemplateRunContext.Set("T2", "P2");

        Assert.Equal(TemplateContextSource.Auto, TemplateRunContext.SourceFor("T1"));
        Assert.Equal(TemplateContextSource.Manual, TemplateRunContext.SourceFor("T2"));
    }

    [Fact]
    public void Context_Should_RaiseChanged_When_OnlyTheSourceDiffers()
    {
        TemplateRunContext.Set("T1", "P1", TemplateContextSource.Auto);

        var seen = new List<string?>();
        Action<string?> handler = id => seen.Add(id);
        TemplateRunContext.Changed += handler;
        try
        {
            // Same pipeline, now promoted to a live run - the chip text changes, so listeners must know.
            TemplateRunContext.Set("T1", "P1", TemplateContextSource.Running);
        }
        finally { TemplateRunContext.Changed -= handler; }

        Assert.Equal(["T1"], seen);
        Assert.Equal(TemplateContextSource.Running, TemplateRunContext.SourceFor("T1"));
    }

    [Fact]
    public void Context_Should_ForgetItsSource_When_Cleared()
    {
        TemplateRunContext.Set("T1", "P1", TemplateContextSource.Running);

        TemplateRunContext.Clear("T1");

        Assert.Null(TemplateRunContext.For("T1"));
        Assert.Equal(TemplateContextSource.Manual, TemplateRunContext.SourceFor("T1"));
    }

    // ── end to end through the real usage scan ──────────────────────────────

    [Fact]
    public void AutoContext_Should_ResolveLibraryTokens_When_APipelineIsRunning()
    {
        var config = new WatchListConfig
        {
            WatchItems =
            [
                new WatchItemConfig
                {
                    Tag = "SP2023R2SP2",
                    Path = @"C:\Triggers",
                    Events = [new EventConfig { Type = "Renamed", Children = [new RefConfig { TemplateID = "T1" }] }],
                },
                new WatchItemConfig
                {
                    Tag = "SP2026",
                    Path = @"C:\Triggers",
                    Events = [new EventConfig { Type = "Renamed", Children = [new RefConfig { TemplateID = "T1" }] }],
                },
            ],
            Templates =
            [
                new TemplateConfig { ID = "T1", Children = [new ActionConfig { Tag = "Install", Command = "[_Installer]" }] },
            ],
        };

        TreeNodeViewModel.TokensFor("SP2023R2SP2")["_Installer"] = @"C:\SP2023R2SP2\Install.bat";
        TreeNodeViewModel.TokensFor("SP2026")["_Installer"] = @"C:\SP2026\Install.bat";

        var candidates = TemplateUsage.PipelinesReferencing(config, "T1");
        var (pipeline, source) = TemplateContextResolver.Decide(candidates, Running("SP2026"));
        TemplateRunContext.Set("T1", pipeline, source);

        var libraryAction = TreeNodeViewModel.FromTemplateList(config.Templates).Children[0].Children[0];

        Assert.Equal(@"C:\SP2026\Install.bat", libraryAction.ResolvedCommand);
        Assert.True(libraryAction.IsCommandResolved);
    }
}
