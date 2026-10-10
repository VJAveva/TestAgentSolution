using TestControllerGrpc.Authorization;
using TestControllerGrpc.Configuration;
using TestControllerGrpc.Core.PipelineBuilder;
using TestControllerGrpc.Identity;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels.PipelineBuilder;

namespace TestControllerGrpc.Tests.PipelineBuilder;

/// <summary>
/// The panel's flow and its authoring gate. Create is Administrator-only, and the two gates have to
/// agree: a button that is enabled but fails on click is the exact shape that bit the scoped-run
/// commands, where <c>CapabilityChecker</c> said yes and <c>AuthorizationService</c> said no.
/// </summary>
public class PipelineBuilderViewModelTests : IDisposable
{
    private readonly string _root;
    private readonly string _parameters;

    public PipelineBuilderViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"BuilderVm_{Guid.NewGuid():N}");
        _parameters = Path.Combine(_root, "Parameters", "SP2026R2");
        var recipes = Path.Combine(_root, "recipes");
        var templates = Path.Combine(_root, "templates");
        Directory.CreateDirectory(_parameters);
        Directory.CreateDirectory(recipes);
        Directory.CreateDirectory(templates);

        File.WriteAllText(Path.Combine(_parameters, "SP2026R2-pipeline-config.json"), """
        {
          "version": 1,
          "global": { "_ReleaseName": "SP2026R2", "_Installer": "C:\\s\\setup.exe" },
          "profiles": { "Sanity": { "_Agent1": "jvgr1" } }
        }
        """);

        File.WriteAllText(Path.Combine(recipes, "smoke.json"), """
        { "name": "SmokeE2E", "stages": [ { "name": "Install", "templateId": "Install", "agentMapping": "pool-fanout" } ] }
        """);

        File.WriteAllText(Path.Combine(templates, "stages.xml"), """
        <Templates>
          <Template ID="Install">
            <Action Type="RunRemoteCommand" Tag="install" AgentName="[_Agent]"
                    Command="C:\Scripts\Install.bat" Parameters="[_ReleaseName]" Timeout="600" />
          </Template>
        </Templates>
        """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        GC.SuppressFinalize(this);
    }

    private sealed class FakeAgents(params string[] names) : IBuilderAgentSource
    {
        public IReadOnlyList<BuilderAgent> GetAgents() =>
            [.. names.Select((n, i) => new BuilderAgent(n, i % 3 == 0 ? "Offline" : "Online", i % 3 != 0, false))];
    }

    private PipelineBuilderViewModel Vm(bool secured, string role, params string[] agents)
    {
        var options = new TestOptionsMonitor<RbacOptions>(new RbacOptions { Enabled = secured });
        var holder = new CurrentUserHolder(options);
        holder.SetUser(MakeAuthUser(role));

        var service = new PipelineBuilderService(
            new DerivedTargetCatalog(Path.Combine(_root, "Parameters")),
            new FolderRecipeSource(Path.Combine(_root, "recipes")),
            new FolderStageTemplateSource(Path.Combine(_root, "templates")),
            BuilderAuth.AdminOnly,
            Path.Combine(_root, "Triggers"));

        return new PipelineBuilderViewModel(
            service,
            new FakeAgents(agents),
            new CapabilityChecker(holder, options),
            holder,
            () => new WatchListConfig(),
            Path.Combine(_root, "fragments"));
    }

    private static AuthUserInfo MakeAuthUser(string role) => new(
        UserId: Guid.NewGuid().ToString("D"),
        Username: "testuser",
        DisplayName: "Test User",
        Role: role,
        ClientKind: ClientKind.Wpf.ToString(),
        Capabilities: PermissionCatalog.GetPermissionsForRole(role).Select(p => p.ToString()).ToList(),
        MustChangePassword: false,
        IsGuest: role == nameof(Role.Guest),
        AssignedPipelineIds: []);

    private sealed class TestOptionsMonitor<T>(T value) : Microsoft.Extensions.Options.IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private static PipelineBuilderViewModel Advance(PipelineBuilderViewModel vm, int agentsToPick)
    {
        vm.SelectedTarget = vm.Targets.First();
        vm.NextCommand.Execute(null);
        vm.SelectedRecipe = vm.Recipes.First();
        vm.NextCommand.Execute(null);
        foreach (var agent in vm.Agents.Take(agentsToPick)) agent.IsSelected = true;
        vm.NextCommand.Execute(null);
        return vm;
    }

    // ── flow ────────────────────────────────────────────────────────────

    [Fact]
    public void Next_Should_NotAdvance_Until_TheStepIsAnswered()
    {
        var vm = Vm(secured: false, nameof(Role.Administrator), "a", "b");

        Assert.False(vm.CanGoNext);
        vm.NextCommand.Execute(null);
        Assert.Equal(BuilderStep.Target, vm.CurrentStep);

        vm.SelectedTarget = vm.Targets.First();
        Assert.True(vm.CanGoNext);
        vm.NextCommand.Execute(null);
        Assert.Equal(BuilderStep.Recipe, vm.CurrentStep);
    }

    [Fact]
    public void Review_Should_PreviewTheChosenAgents()
    {
        var vm = Advance(Vm(false, nameof(Role.Administrator), "a", "b", "c"), agentsToPick: 2);

        Assert.Equal(BuilderStep.Review, vm.CurrentStep);
        Assert.NotNull(vm.Preview);
        Assert.Equal("SP2026R2-SmokeE2E", vm.Preview!.Tag);
        Assert.Contains("\"_Agent2\"", vm.Preview.ConfigJson);
    }

    [Fact]
    public void Back_Should_ReturnToTheAgentStep_FromReview()
    {
        var vm = Advance(Vm(false, nameof(Role.Administrator), "a"), agentsToPick: 1);

        vm.BackCommand.Execute(null);

        Assert.Equal(BuilderStep.Agents, vm.CurrentStep);
    }

    // ── agent picker at scale ───────────────────────────────────────────

    [Fact]
    public void Filter_Should_NarrowTheVisibleRows_ButKeepSelections()
    {
        // Rebuilding rows on every keystroke would silently drop agents already ticked.
        var names = Enumerable.Range(1, 60).Select(i => $"node{i:00}").ToArray();
        var vm = Vm(false, nameof(Role.Administrator), names);

        vm.Agents.First(a => a.Name == "node42").IsSelected = true;
        vm.AgentFilter = "node1";

        Assert.True(vm.VisibleAgents.Count < 60);
        Assert.DoesNotContain(vm.VisibleAgents, a => a.Name == "node42");
        Assert.Equal(1, vm.SelectedAgentCount);
    }

    [Fact]
    public void SelectAllVisible_Should_OnlyTickTheFilteredRows()
    {
        var names = Enumerable.Range(1, 60).Select(i => $"node{i:00}").ToArray();
        var vm = Vm(false, nameof(Role.Administrator), names);

        vm.AgentFilter = "node1";
        var visible = vm.VisibleAgents.Count;
        vm.SelectAllVisibleCommand.Execute(null);

        Assert.Equal(visible, vm.SelectedAgentCount);
    }

    [Fact]
    public void OnlineOnly_Should_HideOfflineAgents()
    {
        var vm = Vm(false, nameof(Role.Administrator), "a", "b", "c", "d", "e", "f");

        vm.OnlineOnly = true;

        Assert.All(vm.VisibleAgents, a => Assert.True(a.IsOnline));
        Assert.Contains(vm.Agents, a => !a.IsOnline);
    }

    [Fact]
    public void Preview_Should_FanOutTo50Agents()
    {
        var names = Enumerable.Range(1, 50).Select(i => $"node{i:00}").ToArray();
        var vm = Advance(Vm(false, nameof(Role.Administrator), names), agentsToPick: 50);

        Assert.Equal(50, vm.SelectedAgentCount);
        Assert.Contains("\"_Agent50\"", vm.Preview!.ConfigJson);
    }

    // ── the authoring gate ──────────────────────────────────────────────

    [Fact]
    public void CanAuthor_Should_BeTrue_ForAnAdministratorInSecuredMode()
    {
        var vm = Vm(secured: true, nameof(Role.Administrator), "a");

        Assert.True(vm.CanAuthor);
    }

    [Theory]
    [InlineData(nameof(Role.SeniorManager))]
    [InlineData(nameof(Role.Engineer))]
    [InlineData(nameof(Role.Guest))]
    public void CanAuthor_Should_BeFalse_ForEveryLesserRole(string role)
    {
        var vm = Vm(secured: true, role, "a");

        Assert.False(vm.CanAuthor);
    }

    [Fact]
    public async Task Create_Should_RefuseAndWriteNothing_When_TheUserCannotAuthor()
    {
        var vm = Advance(Vm(secured: true, nameof(Role.Engineer), "a", "b"), agentsToPick: 2);

        await vm.CreateCommand.ExecuteAsync(null);

        Assert.False(vm.CanCreate);
        Assert.Contains("permission", vm.Status, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(_root, "fragments")));
    }

    [Fact]
    public async Task Create_Should_Write_When_AnAdministratorHasAGreenGate()
    {
        var vm = Advance(Vm(secured: true, nameof(Role.Administrator), "a", "b"), agentsToPick: 2);

        Assert.True(vm.CanCreate, "blocked by: " + string.Join(" | ", vm.Blocking.Select(b => b.Message)));
        await vm.CreateCommand.ExecuteAsync(null);

        Assert.Contains("Import", vm.Status);
        Assert.True(File.Exists(Path.Combine(_root, "fragments", "SP2026R2-SmokeE2E.watchitem.xml")));
    }

    [Theory]
    [InlineData(nameof(Role.Administrator), true)]
    [InlineData(nameof(Role.SeniorManager), false)]
    [InlineData(nameof(Role.Engineer), false)]
    [InlineData(nameof(Role.Guest), false)]
    public async Task TheViewModelGate_Should_AgreeWithTheService(string role, bool expected)
    {
        // An enabled button that fails on click is the exact shape that bit the scoped-run commands,
        // where CapabilityChecker said yes and AuthorizationService said no.
        var vm = Advance(Vm(secured: true, role, "a"), agentsToPick: 1);

        Assert.Equal(expected, vm.CanCreate);

        await vm.CreateCommand.ExecuteAsync(null);
        var written = File.Exists(Path.Combine(_root, "fragments", "SP2026R2-SmokeE2E.watchitem.xml"));

        Assert.Equal(expected, written);
    }

    [Fact]
    public void Review_Should_ShowTheBuildAsInformational_NotBlocking()
    {
        // The split gate, seen from the UI: a missing build must not light up as an error.
        var vm = Advance(Vm(false, nameof(Role.Administrator), "a"), agentsToPick: 1);

        Assert.Empty(vm.Blocking);
        Assert.True(vm.CanCreate);
    }
}
