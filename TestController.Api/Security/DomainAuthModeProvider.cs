using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TestController.Api.Security;

/// <summary>
/// Domain mode authentication provider.
/// Uses Windows Integrated Authentication (Negotiate/Kerberos/NTLM).
/// Resolves roles based on AD group membership.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DomainAuthModeProvider : IAuthenticationModeProvider
{
    private readonly SecurityOptions _options;
    private readonly ILogger<DomainAuthModeProvider> _logger;
    private volatile bool _isHealthy = true;

    public DomainAuthModeProvider(IOptions<SecurityOptions> options, ILogger<DomainAuthModeProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public AuthMode Mode => AuthMode.Domain;
    public bool IsHealthy => _isHealthy;

    public UserRole ResolveRole(ClaimsPrincipal user)
    {
        if (user.Identity is not WindowsIdentity windowsIdentity || !windowsIdentity.IsAuthenticated)
            return UserRole.Anonymous;

        var adminGroup = _options.Domain.AdminGroup;
        var userGroup = _options.Domain.UserGroup;

        try
        {
            if (IsInGroup(windowsIdentity, adminGroup))
                return UserRole.Admin;

            if (IsInGroup(windowsIdentity, userGroup))
                return UserRole.User;

            // Authenticated but not in any known group — treat as basic User
            return UserRole.User;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve AD group membership for {User}", windowsIdentity.Name);
            _isHealthy = false;
            return UserRole.User;
        }
    }

    public string GetDiagnosticStatus()
    {
        return _isHealthy
            ? $"Domain mode active (domain: {_options.Domain.RequireDomain})"
            : $"Domain mode DEGRADED — AD group lookup failed (domain: {_options.Domain.RequireDomain})";
    }

    public void SetHealthy(bool healthy) => _isHealthy = healthy;

    private static bool IsInGroup(WindowsIdentity identity, string groupName)
    {
        if (string.IsNullOrEmpty(groupName)) return false;

        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(groupName);
    }
}
