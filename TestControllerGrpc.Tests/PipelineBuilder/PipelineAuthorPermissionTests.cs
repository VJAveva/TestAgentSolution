using TestControllerGrpc.Authorization;
using TestControllerGrpc.Identity;

namespace TestControllerGrpc.Tests.PipelineBuilder;

/// <summary>
/// <see cref="Permission.Pipeline_Author"/> gates creating a pipeline with the builder. Authoring
/// decides which commands run on which agents, so it is strictly more powerful than triggering a
/// pipeline somebody else wrote - it must not leak below Administrator.
///
/// The enum is persisted in audit rows, so a member may only ever be APPENDED; inserting one
/// renumbers every later member and silently rewrites history.
/// </summary>
public class PipelineAuthorPermissionTests
{
    [Fact]
    public void Pipeline_Author_Should_BeTheLastMember_So_AuditHistoryIsNotRenumbered()
    {
        var all = Enum.GetValues<Permission>();

        Assert.Equal(Permission.Pipeline_Author, all[^1]);
    }

    [Fact]
    public void Pipeline_Author_Should_KeepEveryEarlierMemberAtItsOriginalValue()
    {
        // Spot-checks the boundaries of the three append batches already in the enum.
        Assert.Equal(0, (int)Permission.Pipeline_View);
        Assert.Equal(19, (int)Permission.System_ChangeMode);
        Assert.Equal(20, (int)Permission.CodeChurn_View);
        Assert.Equal(23, (int)Permission.Fleet_InstallUpdates);
    }

    [Fact]
    public void Administrator_Should_HavePipeline_Author()
    {
        var admin = PermissionCatalog.GetPermissionsForRole(Role.Administrator.ToString());

        Assert.Contains(Permission.Pipeline_Author, admin);
    }

    [Theory]
    [InlineData(nameof(Role.SeniorManager))]
    [InlineData(nameof(Role.Engineer))]
    [InlineData(nameof(Role.Guest))]
    public void LesserRoles_Should_NotHavePipeline_Author(string role)
    {
        var permissions = PermissionCatalog.GetPermissionsForRole(role);

        Assert.DoesNotContain(Permission.Pipeline_Author, permissions);
    }
}
