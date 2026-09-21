using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Tests for <see cref="IActionPipelineExecutor.ExecuteResolvedNodeAsync"/> — the node-run dispatcher.
/// Asserts it reuses the existing tracked entry points rather than running its own walk, and that a
/// scoped run touches nothing outside the node.
/// </summary>
public class NodeRunDispatchTests
{
    /// <summary>Records which tracked entry point a node-run reached, and with what node.</summary>
    private sealed class SpyExecutor : IActionPipelineExecutor
    {
        public List<string> Calls { get; } = [];
        public EventConfig? EventRun { get; private set; }
        public ActionGroupConfig? GroupRun { get; private set; }
        public ActionConfig? ActionRun { get; private set; }
        public PipelineExecutionContext? Ctx { get; private set; }

        public event Action<PipelineLogEntry>? LogEntry { add { } remove { } }
        public event Action<IActionNode, string>? NodeProgress { add { } remove { } }
        public event Action<IActionNode, int, string>? NodeFailed { add { } remove { } }

        public void LoadTemplates(IEnumerable<TemplateConfig> templates) { }

        public Task ExecuteEventAsync(EventConfig ev, PipelineExecutionContext ctx, CancellationToken ct)
            => throw new InvalidOperationException("Untracked path must not be used for a node-run.");
        public Task<bool> ExecuteGroupAsync(ActionGroupConfig g, PipelineExecutionContext ctx, CancellationToken ct)
            => throw new InvalidOperationException("Untracked path must not be used for a node-run.");
        public Task<bool> ExecuteSingleActionAsync(ActionConfig a, PipelineExecutionContext ctx, CancellationToken ct)
            => throw new InvalidOperationException("Untracked path must not be used for a node-run.");
        public Task RetryFailedAsync(ExecutionSession previousSession, CancellationToken ct)
            => throw new InvalidOperationException("Retry must not be used for a node-run.");

        public Task ExecuteEventTrackedAsync(string tag, EventConfig evt, PipelineExecutionContext ctx, CancellationToken ct)
        {
            Calls.Add(nameof(ExecuteEventTrackedAsync));
            EventRun = evt; Ctx = ctx;
            return Task.CompletedTask;
        }

        public Task<bool> ExecuteGroupTrackedAsync(string tag, ActionGroupConfig group, PipelineExecutionContext ctx, CancellationToken ct)
        {
            Calls.Add(nameof(ExecuteGroupTrackedAsync));
            GroupRun = group; Ctx = ctx;
            return Task.FromResult(true);
        }

        public Task<bool> ExecuteSingleActionTrackedAsync(string tag, ActionConfig action, PipelineExecutionContext ctx, CancellationToken ct)
        {
            Calls.Add(nameof(ExecuteSingleActionTrackedAsync));
            ActionRun = action; Ctx = ctx;
            return Task.FromResult(true);
        }

        public Task ExecuteTemplateTrackedAsync(string tag, TemplateConfig template, PipelineExecutionContext ctx, CancellationToken ct)
        {
            Calls.Add(nameof(ExecuteTemplateTrackedAsync));
            return Task.CompletedTask;
        }
    }

    private static WatchItemConfig BuildItem() => new()
    {
        Tag = "Pipe",
        Events =
        [
            new EventConfig
            {
                Type = "Renamed",
                Children =
                [
                    new ActionConfig { Tag = "first", AgentName = "a1" },
                    new ActionGroupConfig
                    {
                        Tag = "Phase1",
                        Children =
                        [
                            new ActionConfig { Tag = "inner", AgentName = "a2" },
                        ],
                    },
                    new RefConfig { TemplateID = "Tpl" },
                    new ActionConfig { Tag = "last", AgentName = "a3" },
                ],
            },
        ],
    };

    private static async Task<SpyExecutor> RunAsync(string path, NodeRunScope scope = NodeRunScope.OnlyThisNode)
    {
        var item = BuildItem();
        Assert.True(NodeAddressing.TryResolve(item, path, out var resolved, out var error), error);

        var initialize = scope == NodeRunScope.NodeWithInitialize
            ? NodeAddressing.NearestInitializeFor(item, path)
            : null;

        var spy = new SpyExecutor();
        // ExecuteResolvedNodeAsync is a default interface method, so it is reachable only
        // through the interface — never off the concrete type.
        await ((IActionPipelineExecutor)spy).ExecuteResolvedNodeAsync(
            item.Tag, resolved!, initialize,
            new PipelineExecutionContext { WatchItemTag = item.Tag },
            CancellationToken.None);
        return spy;
    }

    [Fact]
    public async Task ExecuteResolvedNodeAsync_Should_UseEventTrackedPath_When_NodeIsAnEvent()
    {
        var spy = await RunAsync("e0");

        Assert.Equal(["ExecuteEventTrackedAsync"], spy.Calls);
        Assert.Equal("Renamed", spy.EventRun?.Type);
    }

    [Fact]
    public async Task ExecuteResolvedNodeAsync_Should_UseGroupTrackedPath_When_NodeIsAGroup()
    {
        var spy = await RunAsync("e0/c1");

        Assert.Equal(["ExecuteGroupTrackedAsync"], spy.Calls);
        Assert.Equal("Phase1", spy.GroupRun?.Tag);
    }

    [Fact]
    public async Task ExecuteResolvedNodeAsync_Should_RunOnlyThatAction_When_NodeIsAnAction()
    {
        var spy = await RunAsync("e0/c1/c0");

        Assert.Equal(["ExecuteSingleActionTrackedAsync"], spy.Calls);
        Assert.Equal("inner", spy.ActionRun?.Tag);
        // Isolation: the siblings before and after it were never dispatched.
        Assert.Null(spy.EventRun);
        Assert.Null(spy.GroupRun);
    }

    [Fact]
    public async Task ExecuteResolvedNodeAsync_Should_WrapRefInAGroup_When_NodeIsATemplateRef()
    {
        var spy = await RunAsync("e0/c2");

        // Routed through the group path so the executor's own Ref expansion resolves the template.
        Assert.Equal(["ExecuteGroupTrackedAsync"], spy.Calls);
        Assert.Equal("Tpl", spy.GroupRun?.Tag);
        var only = Assert.Single(spy.GroupRun!.Children);
        Assert.Equal("Tpl", Assert.IsType<RefConfig>(only).TemplateID);
    }

    [Fact]
    public async Task ExecuteResolvedNodeAsync_Should_NotLoadParameters_When_ScopeIsOnlyThisNode()
    {
        var spy = await RunAsync("e0/c0");

        Assert.Empty(spy.Ctx!.Parameters);
    }

    [Fact]
    public async Task ExecuteResolvedNodeAsync_Should_LoadOwningInitialize_When_ScopeIncludesIt()
    {
        var file = Path.Combine(Path.GetTempPath(), $"node-run-init-{Guid.NewGuid():N}.txt");
        await File.WriteAllLinesAsync(file, ["_Seeded,fromInitialize"]);
        try
        {
            var item = BuildItem();
            item.Events[0].Children.Insert(0, new InitializeConfig { Tag = "Init", ParameterFile = file });

            // The action moved to c1 once Initialize was inserted at the front.
            Assert.True(NodeAddressing.TryResolve(item, "e0/c1", out var resolved, out _));
            var initialize = NodeAddressing.NearestInitializeFor(item, "e0/c1");
            Assert.NotNull(initialize);

            var spy = new SpyExecutor();
            await ((IActionPipelineExecutor)spy).ExecuteResolvedNodeAsync(
                item.Tag, resolved!, initialize,
                new PipelineExecutionContext { WatchItemTag = item.Tag },
                CancellationToken.None);

            Assert.Equal(["ExecuteSingleActionTrackedAsync"], spy.Calls);
            Assert.Equal("fromInitialize", spy.Ctx!.Parameters["_Seeded"]);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
