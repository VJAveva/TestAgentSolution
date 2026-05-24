using System.Security.Claims;

namespace TestController.Api.Security;

/// <summary>
/// Evaluates whether the current user is authorized to interact with a specific agent,
/// based on the AllowedAgents claim from token-based authentication.
/// </summary>
public static class AgentScopeAuthorization
{
    /// <summary>Claim type used to store allowed agent names (comma-separated).</summary>
    public const string AllowedAgentsClaimType = "AllowedAgents";

    /// <summary>
    /// Checks whether the current user is allowed to interact with the specified agent.
    /// Returns true if:
    /// - User has Admin role (admins can access all agents)
    /// - User has no AllowedAgents claim (unrestricted)
    /// - The specified agent is in the user's AllowedAgents list
    /// </summary>
    public static bool IsAuthorizedForAgent(ClaimsPrincipal user, string agentName)
    {
        if (string.IsNullOrWhiteSpace(agentName)) return true;

        // Admins can access all agents
        if (user.IsInRole("Admin")) return true;

        // Check AllowedAgents claim
        var allowedClaim = user.FindFirst(AllowedAgentsClaimType);
        if (allowedClaim is null || string.IsNullOrWhiteSpace(allowedClaim.Value))
            return true; // No restriction claim = unrestricted

        var allowedAgents = allowedClaim.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return allowedAgents.Any(a => string.Equals(a, agentName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Filters a collection of agent names to only those the user is authorized to access.
    /// </summary>
    public static IEnumerable<string> FilterAuthorizedAgents(ClaimsPrincipal user, IEnumerable<string> agentNames)
    {
        if (user.IsInRole("Admin")) return agentNames;

        var allowedClaim = user.FindFirst(AllowedAgentsClaimType);
        if (allowedClaim is null || string.IsNullOrWhiteSpace(allowedClaim.Value))
            return agentNames; // No restriction

        var allowedAgents = allowedClaim.Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return agentNames.Where(a => allowedAgents.Contains(a));
    }
}
