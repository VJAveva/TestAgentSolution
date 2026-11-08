using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using TestControllerGrpc.Ado;
using TestControllerGrpc.Ado.Dto;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Features;
using TestControllerGrpc.Identity;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Features;

public sealed class CodeChurnRepositoryTests : IDisposable
{
    private const string Project = "AppServer OMI";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"CCR_{Guid.NewGuid():N}");
    private readonly string _file;

    private const string TwoRepos = """
        {
          "TeamName": "WAS-QA",
          "Features": { },
          "CodeChurn": {
            "Repositories": [
              { "Name": "TeamX.Alpha", "Url": "https://dev.azure.com/AVEVA-VSTS/AppServer%20OMI/_git/TeamX.Alpha",
                "Branches": ["main", "release/2026"], "IncludePaths": ["src/"], "ExcludePaths": ["src/tests/"], "CredentialRef": "ADO_PAT" },
              { "Name": "TeamX.Beta", "Branches": ["develop"] }
            ]
          }
        }
        """;

    public CodeChurnRepositoryTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "Features.json");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private FeatureGate Gate(string json)
    {
        File.WriteAllText(_file, json);
        return new FeatureGate(_file, watch: false);
    }

    private static ComponentBuildInfo Component(string id, int def, string repo) => new(id, def, 1, repo, [], []);

    private static IComponentBuildMap Map(params ComponentBuildInfo[] components)
    {
        var map = new Mock<IComponentBuildMap>();
        map.Setup(m => m.All()).Returns(components);
        return map.Object;
    }

    private static IOptions<AdoOptions> Ado(bool enabled = false) => Options.Create(new AdoOptions
    {
        Enabled = enabled, Organization = "AVEVA-VSTS", Project = "System Platform", OmiProject = Project,
    });

    // ── Config ─────────────────────────────────────────────────────

    [Fact]
    public void Gate_Should_LoadEachRepositoryWithItsOwnBranches_When_TwoAreConfigured()
    {
        using var gate = Gate(TwoRepos);

        var repos = gate.CodeChurnRepositories!;
        Assert.Equal(["TeamX.Alpha", "TeamX.Beta"], repos.Select(r => r.Name));
        Assert.Equal(["main", "release/2026"], repos[0].Branches);
        Assert.Equal(["develop"], repos[1].Branches);
        Assert.Equal("ADO_PAT", repos[0].CredentialRef);
    }

    [Fact]
    public void Gate_Should_ReportNotConfigured_When_TheSectionIsAbsent()
    {
        using var gate = Gate("""{ "Features": { } }""");

        Assert.Null(gate.CodeChurnRepositories);
        Assert.True(new CodeChurnScope(gate).IncludesComponent(Component("Any", 1, "Whatever")));
    }

    [Fact]
    public void Gate_Should_SkipTheEntry_When_CredentialRefHoldsATokenInsteadOfAName()
    {
        var pat = new string('a', 52);
        using var gate = Gate($$"""{ "CodeChurn": { "Repositories": [ { "Name": "R", "Branches": ["main"], "CredentialRef": "{{pat}}" } ] } }""");

        Assert.Empty(gate.CodeChurnRepositories!);
    }

    [Theory]
    [InlineData("/src/Alpha/a.cs", true)]
    [InlineData("/src/tests/a.cs", false)]
    [InlineData("/docs/readme.md", false)]
    public void InScope_Should_ApplyIncludeThenExclude_When_PathsAreConfigured(string path, bool expected)
    {
        var repo = new CodeChurnRepository("R", null, ["main"], ["src/"], ["src/tests/"], null);

        Assert.Equal(expected, CodeChurnRepositoryRules.InScope(repo, path));
    }

    [Fact]
    public void InScope_Should_MatchGlobs_When_EntryHasWildcards()
    {
        var repo = new CodeChurnRepository("R", null, ["main"], ["*.cs"], ["*Designer.cs"], null);

        Assert.True(CodeChurnRepositoryRules.InScope(repo, "/src/a/Form.cs"));
        Assert.False(CodeChurnRepositoryRules.InScope(repo, "/src/a/Form.Designer.cs"));
        Assert.False(CodeChurnRepositoryRules.InScope(repo, "/src/a/readme.md"));
    }

    // ── Save + audit ───────────────────────────────────────────────

    [Fact]
    public async Task SaveCodeChurnAsync_Should_PersistBackUpAndAudit_When_AdminAddsARepository()
    {
        using var gate = Gate("""{ "TeamName": "WAS-QA", "Features": { "Results": false }, "CodeChurn": { "Other": 1 } }""");
        var audit = new List<AuditEntry>();
        var writer = new Mock<IAuditWriter>();
        writer.Setup(w => w.Enqueue(It.IsAny<AuditEntry>())).Callback<AuditEntry>(audit.Add);
        var service = new FeatureSettingsService(gate, writer.Object, clock: () => new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero));
        var admin = new SyntheticUserContext("u-1", "Ada Admin", ClientKind.Web, ["Administrator"]);

        var result = await service.SaveCodeChurnAsync(
            [new CodeChurnRepository("TeamX.Alpha", null, ["main"], [], [], "ADO_PAT")], admin, null, default);

        Assert.True(result.Success, result.Message);
        Assert.Equal(_file + ".bak-20261107-090000", result.BackupPath);
        Assert.Equal(["TeamX.Alpha"], gate.CodeChurnRepositories!.Select(r => r.Name));
        using var restarted = new FeatureGate(_file, watch: false);
        Assert.Equal(["main"], restarted.CodeChurnRepositories!.Single().Branches);
        Assert.False(restarted.IsEnabled(FeatureKeys.Results));
        Assert.Contains("\"Other\": 1", File.ReadAllText(_file));
        var entry = Assert.Single(audit);
        Assert.Equal(nameof(Permission.System_ConfigureFeatures), entry.ActionName);
        Assert.Equal("CodeChurn.Repositories/TeamX.Alpha", entry.ResourceId);
        Assert.Equal("codechurn-added", entry.ReasonCode);
    }

    [Fact]
    public void SaveCodeChurn_Should_Refuse_When_TheTokenIsPastedAsCredentialRef()
    {
        var result = FeatureFileWriter.SaveCodeChurn(_file,
            [new CodeChurnRepository("R", null, ["main"], [], [], new string('x', 84))]);

        Assert.False(result.Success);
        Assert.Contains("looks like a token", result.Message);
        Assert.False(File.Exists(_file));
    }

    // ── Scope in the UI ────────────────────────────────────────────

    [Fact]
    public async Task Branches_Should_BeThoseOfTheChosenRepository_When_RepositoriesAreConfigured()
    {
        using var gate = Gate(TwoRepos);
        var catalog = new RegressionSourceCatalog(Ado(), Map(), new ServiceCollection().BuildServiceProvider(), new CodeChurnScope(gate));

        Assert.Equal(["main", "release/2026"], await catalog.GetBranchesAsync("TeamX.Alpha", default));
        Assert.Equal(["develop"], await catalog.GetBranchesAsync("TeamX.Beta", default));
        Assert.Equal(["develop", "main", "release/2026"], await catalog.GetBranchesAsync((string?)null, default));
    }

    [Fact]
    public void ConnectionWarning_Should_SayNoRepositoriesConfigured_When_TheListIsEmpty()
    {
        using var gate = Gate("""{ "CodeChurn": { "Repositories": [] } }""");
        var catalog = new RegressionSourceCatalog(Ado(), Map(Component("A", 1, "TeamX.Alpha")),
            new ServiceCollection().BuildServiceProvider(), new CodeChurnScope(gate));

        Assert.Equal(RegressionSourceCatalog.NoConfiguredRepositoriesMessage, catalog.GetConnectionInfo().Warning);
        Assert.Empty(catalog.GetComponents());
    }

    // ── Validation ─────────────────────────────────────────────────

    [Fact]
    public async Task Validate_Should_NameEachProblem_When_RepoBranchUrlOrCredentialIsWrong()
    {
        using var gate = Gate("""
            {
              "CodeChurn": { "Repositories": [
                { "Name": "TeamX.Alpha", "Url": "https://dev.azure.com/OtherOrg/Elsewhere/_git/TeamX.Alpha",
                  "Branches": ["main", "release/missing"], "CredentialRef": "CCR_UNSET_VARIABLE_FOR_TEST" },
                { "Name": "TeamX.Gone", "Branches": ["main"] }
              ] }
            }
            """);
        var git = new Mock<IGitQueries>();
        git.Setup(g => g.GetAllRepositoriesAsync(Project, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AdoRepositoryDto { Id = "r-1", Name = "TeamX.Alpha" }]);
        git.Setup(g => g.GetBranchNamesAsync(Project, "r-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["main", "develop"]);
        var services = new ServiceCollection().AddSingleton(git.Object).BuildServiceProvider();
        var validator = new CodeChurnRepositoryValidator(new CodeChurnScope(gate),
            Map(Component("Alpha", 101, "TeamX.Alpha")), Ado(enabled: true), services);

        var result = await validator.ValidateAsync(refresh: true, default);

        var alpha = result.Single(r => r.Name == "TeamX.Alpha");
        Assert.Contains(alpha.Problems, p => p.Contains("Branch 'release/missing' does not exist"));
        Assert.DoesNotContain(alpha.Problems, p => p.Contains("'main'"));
        Assert.Contains(alpha.Problems, p => p.Contains("Url points at 'OtherOrg/Elsewhere'"));
        Assert.Contains(alpha.Problems, p => p.Contains("Credential 'CCR_UNSET_VARIABLE_FOR_TEST' is not set"));
        var gone = result.Single(r => r.Name == "TeamX.Gone");
        Assert.Contains(gone.Problems, p => p.Contains("not found in Azure DevOps project"));
        Assert.Contains(gone.Problems, p => p.Contains("No component in the component map"));
    }

    [Fact]
    public async Task Validate_Should_SayAdoIsUnreachable_When_TheRepositoryLookupFails()
    {
        using var gate = Gate(TwoRepos);
        var git = new Mock<IGitQueries>();
        git.Setup(g => g.GetAllRepositoriesAsync(Project, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Net.Http.HttpRequestException("No such host is known."));
        var services = new ServiceCollection().AddSingleton(git.Object).BuildServiceProvider();
        var validator = new CodeChurnRepositoryValidator(new CodeChurnScope(gate),
            Map(Component("Alpha", 101, "TeamX.Alpha"), Component("Beta", 202, "TeamX.Beta")), Ado(enabled: true), services);

        var result = await validator.ValidateAsync(refresh: true, default);

        Assert.All(result, r => Assert.Contains(r.Problems, p => p.Contains("could not be reached")));
    }

    [Fact]
    public async Task Validate_Should_NeverExposeTheCredentialValue_When_TheVariableIsSet()
    {
        const string secret = "ccr-secret-value-must-not-leak";
        var name = $"CCR_PAT_{Guid.NewGuid():N}"[..30];
        Environment.SetEnvironmentVariable(name, secret);
        try
        {
            using var gate = Gate($$"""{ "CodeChurn": { "Repositories": [ { "Name": "TeamX.Alpha", "Branches": ["main"], "CredentialRef": "{{name}}" } ] } }""");
            var logged = new List<string>();
            var logger = new Mock<IAppLogger>();
            logger.Setup(l => l.Warn(It.IsAny<string>(), It.IsAny<string>())).Callback<string, string>((_, m) => logged.Add(m));
            logger.Setup(l => l.Info(It.IsAny<string>(), It.IsAny<string>())).Callback<string, string>((_, m) => logged.Add(m));
            var validator = new CodeChurnRepositoryValidator(new CodeChurnScope(gate), Map(), Ado(),
                new ServiceCollection().BuildServiceProvider(), logger.Object);

            var result = await validator.ValidateAsync(refresh: true, default);

            Assert.True(result.Single().CredentialSet);
            Assert.DoesNotContain(secret, JsonSerializer.Serialize(result));
            Assert.DoesNotContain(logged, m => m.Contains(secret));
            Assert.NotEmpty(logged); // the "no component uses this repository" warning was logged
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    // ── Collection ─────────────────────────────────────────────────

    private static SubsystemRow Row(string component, string repository) => new(
        component, component, RegressionCategoryKind.Runtime, RegressionEvidenceKind.Declared, [], 0,
        [new RegressionChangeRef("c1", "change", DateTimeOffset.UtcNow, [], [], RegressionChangeKind.Commit, null)],
        "succeeded", [], [], 0, true, Repository: repository);

    [Fact]
    public void RepositoryFilter_Should_KeepOnlyThatRepositorysRows_When_ARepositoryIsPicked()
    {
        IReadOnlyList<SubsystemRow> rows = [Row("Alpha", "TeamX.Alpha"), Row("Beta", "TeamX.Beta")];

        Assert.Equal(["Beta"], AdoRegressionDataProvider.ForRepository(rows, "teamx.beta").Select(r => r.Component));
        Assert.Equal(2, AdoRegressionDataProvider.ForRepository(rows, null).Count);
        Assert.Equal(["Alpha"], TestControllerGrpc.Ado.Reporting.RegressionRowFilter.Apply(rows,
            new TestControllerGrpc.Ado.Reporting.RegressionRowFilterOptions { Repository = "TeamX.Alpha", ShowAllChanges = true })
            .Select(r => r.Component));
    }

    [Fact]
    public async Task Collect_Should_ScanOnlyConfiguredRepositoriesAndPaths_When_RepositoriesAreConfigured()
    {
        using var gate = Gate("""
            { "CodeChurn": { "Repositories": [
              { "Name": "TeamX.Alpha", "Branches": ["main"], "IncludePaths": ["src/"], "ExcludePaths": ["src/tests/"] }
            ] } }
            """);
        var builds = new Mock<IBuildQueries>();
        builds.Setup(b => b.GetLatestBuildsByDefinitionAsync(Project, It.IsAny<int>(), 1, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, int def, int _, string? _, CancellationToken _) =>
                [new AdoBuildDto { Id = def, BuildNumber = $"b{def}", Result = "succeeded" }]);
        builds.Setup(b => b.GetBuildChangesAsync(Project, 101, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new AdoBuildChangeDto { Id = "c-src", Message = "Fix engine", Type = "commit" },
                new AdoBuildChangeDto { Id = "c-docs", Message = "Docs only", Type = "commit" },
                new AdoBuildChangeDto { Id = "c-tests", Message = "Tests only", Type = "commit" },
            ]);
        builds.Setup(b => b.GetBuildChangesAsync(Project, 202, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AdoBuildChangeDto { Id = "c-beta", Message = "Beta change", Type = "commit" }]);
        builds.Setup(b => b.GetBuildWorkItemIdsAsync(Project, It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var git = new Mock<IGitQueries>();
        git.Setup(g => g.GetAllRepositoriesAsync(Project, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AdoRepositoryDto { Id = "r-a", Name = "TeamX.Alpha" }, new AdoRepositoryDto { Id = "r-b", Name = "TeamX.Beta" }]);
        git.Setup(g => g.GetCommitChangesAsync(Project, "r-a", "c-src", It.IsAny<CancellationToken>())).ReturnsAsync([("/src/engine.cs", "edit")]);
        git.Setup(g => g.GetCommitChangesAsync(Project, "r-a", "c-docs", It.IsAny<CancellationToken>())).ReturnsAsync([("/docs/readme.md", "edit")]);
        git.Setup(g => g.GetCommitChangesAsync(Project, "r-a", "c-tests", It.IsAny<CancellationToken>())).ReturnsAsync([("/src/tests/t.cs", "edit")]);
        git.Setup(g => g.GetSolutionNamesAsync(Project, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var workItems = new Mock<IWorkItemQueries>();
        workItems.Setup(w => w.GetWithRelationsAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var collector = new ComponentChangeCollector(builds.Object, git.Object, workItems.Object,
            Map(Component("Alpha", 101, "TeamX.Alpha"), Component("Beta", 202, "TeamX.Beta")),
            new Mock<IComponentCategorizer>().Object, Ado(enabled: true), new Mock<IAppLogger>().Object, new CodeChurnScope(gate));

        var (rows, _) = await collector.CollectAsync(null, null, "main", default);

        var row = Assert.Single(rows);
        Assert.Equal("TeamX.Alpha", row.Repository);
        Assert.Equal(["c-src"], row.Changes.Select(c => c.ChangeId));
        Assert.Equal(["/src/engine.cs"], row.FilesModified);
        builds.Verify(b => b.GetBuildChangesAsync(Project, 202, It.IsAny<CancellationToken>()), Times.Never);
    }
}
