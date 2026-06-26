using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Resolves a CI name to its owning team for the failures table.
/// Mapping comes from BuildReportCard:Owners in appsettings.json; falls back
/// to a "{ci}-team" slug when no explicit mapping exists.
/// </summary>
public sealed class CiOwnerResolver
{
    private readonly BuildReportCardConfig _config;

    public CiOwnerResolver(BuildReportCardConfig config) => _config = config;

    public string ResolveTeam(string ciName)
    {
        if (!string.IsNullOrWhiteSpace(ciName)
            && _config.Owners.TryGetValue(ciName, out var owner)
            && !string.IsNullOrWhiteSpace(owner.Team))
        {
            return owner.Team;
        }

        return string.IsNullOrWhiteSpace(ciName)
            ? "unassigned"
            : $"{ciName.ToLowerInvariant()}-team";
    }

    public string ResolveEmail(string ciName)
    {
        if (!string.IsNullOrWhiteSpace(ciName)
            && _config.Owners.TryGetValue(ciName, out var owner))
        {
            return owner.Email;
        }
        return "";
    }
}
