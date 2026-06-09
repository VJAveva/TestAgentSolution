using System.Security.Claims;

namespace TestController.Api.Security;

/// <summary>
/// Abstracts the authentication mode selection and identity resolution.
/// Implementors configure ASP.NET Core authentication for their specific mechanism.
/// </summary>
public interface IAuthenticationModeProvider
{
    /// <summary>The active authentication mode.</summary>
    AuthMode Mode { get; }

    /// <summary>Whether the provider is currently healthy (e.g., AD reachable).</summary>
    bool IsHealthy { get; }

    /// <summary>Resolves the effective role for a given claims principal.</summary>
    UserRole ResolveRole(ClaimsPrincipal user);

    /// <summary>Gets a diagnostic description of the provider state.</summary>
    string GetDiagnosticStatus();
}

/// <summary>Effective role resolved from authentication claims.</summary>
public enum UserRole
{
    Anonymous,
    User,
    Admin
}
