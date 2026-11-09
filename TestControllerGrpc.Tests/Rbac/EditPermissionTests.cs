using Microsoft.EntityFrameworkCore;
using TestController.Persistence;
using TestController.Persistence.Audit;
using TestController.Persistence.Authorization;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Configuration;
using TestControllerGrpc.Editing;
using TestControllerGrpc.Identity;
using TestControllerGrpc.Menus;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Tests.Rbac;

/// <summary>ContextMenu_Requirement.md P4: Pipeline_Edit (scoped by assignment) and Template_Edit (admin only).</summary>
public class EditPermissionTests : IDisposable
{
    private readonly OrchestratorDbContext _db;
    private readonly AuthorizationService _sut;

    public EditPermissionTests()
    {
        var dbOptions = new DbContextOptionsBuilder<OrchestratorDbContext>().UseSqlite("Data Source=:memory:").Options;
        _db = new OrchestratorDbContext(dbOptions);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
        _sut = new AuthorizationService(
            new TestOptionsMonitor<RbacOptions>(new RbacOptions { Enabled = true }),
            new TestDbContextFactory(_db), new QueuedAuditWriter());
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private static SyntheticUserContext User(Role role, params string[] assigned) =>
        new($"{role}-id", role.ToString(), ClientKind.Wpf, [role.ToString()], assigned.ToHashSet());

    [Fact]
    public async Task PipelineEdit_Should_AllowOnlyAssignedPipelines_When_Engineer()
    {
        var engineer = User(Role.Engineer, "P1");

        Assert.True((await _sut.CanAsync(engineer, Permission.Pipeline_Edit, "P1")).Allowed);
        Assert.False((await _sut.CanAsync(engineer, Permission.Pipeline_Edit, "P2")).Allowed);
    }

    [Fact]
    public async Task TemplateEdit_Should_AllowOnlyAdministrators_When_Checked()
    {
        Assert.True((await _sut.CanAsync(User(Role.Administrator), Permission.Template_Edit)).Allowed);
        Assert.False((await _sut.CanAsync(User(Role.SeniorManager), Permission.Template_Edit)).Allowed);
        Assert.False((await _sut.CanAsync(User(Role.Engineer, "P1"), Permission.Template_Edit)).Allowed);
    }

    [Fact]
    public void BlockedReason_Should_BeNull_When_TheUserMayEditAnIdlePipeline()
    {
        Assert.Null(EditAuthorization.BlockedReason(EditTarget.Pipeline("P1"), (_, _) => true, _ => false));
    }

    [Fact]
    public void BlockedReason_Should_ExplainWhy_When_RunningOrNotAllowed()
    {
        Assert.Equal(EditAuthorization.PipelineRunning,
            EditAuthorization.BlockedReason(EditTarget.Pipeline("P1"), (_, _) => true, _ => true));
        Assert.Equal("You do not have edit rights on 'P1'.",
            EditAuthorization.BlockedReason(EditTarget.Pipeline("P1"), (_, _) => false, _ => false));
        Assert.Equal(EditAuthorization.TemplateAdminOnly,
            EditAuthorization.BlockedReason(EditTarget.Template, (p, _) => p != Permission.Template_Edit, _ => false));
    }

    [Fact]
    public void Menu_Should_DisableEditItemsButKeepRun_When_EditIsBlocked()
    {
        var items = NodeMenuProvider.Build(new NodeMenuContext
        {
            NodeKind = NodeKinds.ActionGroup,
            IsSkippable = true,
            CanMoveUp = true,
            EditBlockedReason = EditAuthorization.PipelineRunning,
        });

        Assert.True(items.Single(i => i.Id == NodeMenuIds.ExecuteGroup).IsEnabled);
        Assert.All(items.Where(i => NodeMenuProvider.EditIds.Contains(i.Id)), i =>
        {
            Assert.False(i.IsEnabled);
            Assert.Equal(EditAuthorization.PipelineRunning, i.ToolTip);
        });
    }

    private sealed class TestOptionsMonitor<T>(T value) : Microsoft.Extensions.Options.IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class TestDbContextFactory(OrchestratorDbContext seedDb) : IDbContextFactory<OrchestratorDbContext>
    {
        private readonly DbContextOptions<OrchestratorDbContext> _options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseSqlite((Microsoft.Data.Sqlite.SqliteConnection)seedDb.Database.GetDbConnection()).Options;

        public OrchestratorDbContext CreateDbContext() => new(_options);
    }
}
