using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Tests for <see cref="CiOwnerResolver"/>: maps a CI name to its owning team,
/// falling back to a "{ci}-team" slug when unmapped.
/// </summary>
public class CiOwnerResolverTests
{
    [Fact]
    public void ResolveTeam_Should_ReturnMappedTeam_When_OwnerConfigured()
    {
        var config = new BuildReportCardConfig();
        config.Owners["LegacyCI"] = new CiOwner { Team = "platform-team", Email = "platform@aveva.com" };
        var resolver = new CiOwnerResolver(config);

        Assert.Equal("platform-team", resolver.ResolveTeam("LegacyCI"));
        Assert.Equal("platform@aveva.com", resolver.ResolveEmail("LegacyCI"));
    }

    [Fact]
    public void ResolveTeam_Should_ReturnSlugFallback_When_OwnerNotConfigured()
    {
        var resolver = new CiOwnerResolver(new BuildReportCardConfig());

        Assert.Equal("symbolwizard-team", resolver.ResolveTeam("SymbolWizard"));
    }

    [Fact]
    public void ResolveTeam_Should_ReturnUnassigned_When_CiNameBlank()
    {
        var resolver = new CiOwnerResolver(new BuildReportCardConfig());

        Assert.Equal("unassigned", resolver.ResolveTeam(""));
    }
}
