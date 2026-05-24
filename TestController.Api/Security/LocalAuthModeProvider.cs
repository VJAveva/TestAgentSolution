using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TestController.Api.Security;

/// <summary>
/// Local mode authentication provider.
/// Validates credentials against the local Windows SAM database.
/// Resolves roles based on local group membership.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LocalAuthModeProvider : IAuthenticationModeProvider
{
    private readonly SecurityOptions _options;
    private readonly ILogger<LocalAuthModeProvider> _logger;

    public LocalAuthModeProvider(IOptions<SecurityOptions> options, ILogger<LocalAuthModeProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public AuthMode Mode => AuthMode.Local;
    public bool IsHealthy => true; // Local auth is always available

    public UserRole ResolveRole(ClaimsPrincipal user)
    {
        if (user.Identity is not WindowsIdentity windowsIdentity || !windowsIdentity.IsAuthenticated)
            return UserRole.Anonymous;

        var adminGroup = _options.Local.AdminGroup;

        try
        {
            var principal = new WindowsPrincipal(windowsIdentity);
            if (principal.IsInRole(adminGroup))
                return UserRole.Admin;

            return UserRole.User;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve local group membership for {User}", windowsIdentity.Name);
            return UserRole.User;
        }
    }

    public string GetDiagnosticStatus() => "Local mode active";
}
