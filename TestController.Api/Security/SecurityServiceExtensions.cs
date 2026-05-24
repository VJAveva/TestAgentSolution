using System.Runtime.Versioning;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TestController.Api.Security;

/// <summary>
/// Extension methods to register the multi-identity security framework.
/// </summary>
public static class SecurityServiceExtensions
{
    /// <summary>
    /// Registers authentication, authorization, and security services based on the
    /// configured authentication mode in appsettings.json "Security" section.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddMultiIdentitySecurity(this IServiceCollection services, IConfiguration configuration)
    {
        // Bind configuration
        services.Configure<SecurityOptions>(configuration.GetSection(SecurityOptions.SectionName));

        var securityOptions = new SecurityOptions();
        configuration.GetSection(SecurityOptions.SectionName).Bind(securityOptions);

        // Register mode-specific authentication
        switch (securityOptions.AuthMode)
        {
            case AuthMode.None:
                // No authentication — all requests are treated as authenticated Admin
                services.AddAuthentication("None")
                    .AddScheme<AuthenticationSchemeOptions, NoneAuthenticationHandler>("None", null);
                services.AddSingleton<IAuthenticationModeProvider, NoneAuthModeProvider>();
                break;

            case AuthMode.Domain:
                services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
                    .AddNegotiate();
                services.AddSingleton<IAuthenticationModeProvider, DomainAuthModeProvider>();
                break;

            case AuthMode.Local:
                services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
                    .AddNegotiate();
                services.AddSingleton<IAuthenticationModeProvider, LocalAuthModeProvider>();
                break;

            case AuthMode.Token:
                services.AddAuthentication("Bearer")
                    .AddScheme<TokenAuthenticationOptions, TokenAuthenticationHandler>("Bearer", null);
                services.AddSingleton<ITokenStore, DpapiTokenStore>();
                services.AddSingleton<IAuthenticationModeProvider, TokenAuthModeProvider>();
                break;

            default:
                // Default to Domain mode
                services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
                    .AddNegotiate();
                services.AddSingleton<IAuthenticationModeProvider, DomainAuthModeProvider>();
                break;
        }

        // Always register ITokenStore (required by token management endpoints even in non-Token modes)
        if (securityOptions.AuthMode != AuthMode.Token)
        {
            services.AddSingleton<ITokenStore, InMemoryTokenStore>();
        }

        // Register authorization policies
        services.AddAuthorizationBuilder()
            .AddPolicy(SecurityPolicies.Admin, policy =>
                policy.RequireAuthenticatedUser()
                      .AddRequirements(new AdminRoleRequirement()))
            .AddPolicy(SecurityPolicies.User, policy =>
                policy.RequireAuthenticatedUser())
            .AddPolicy(SecurityPolicies.Anonymous, policy =>
                policy.RequireAssertion(_ => true))
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());

        // Register custom authorization handler
        services.AddSingleton<IAuthorizationHandler, AdminRoleHandler>();
        services.AddSingleton<ISessionOwnershipChecker, SessionOwnershipChecker>();
        services.AddSingleton<ISecurityAuditLogger, SecurityAuditLogger>();

        // Transport security: TLS channel factory for gRPC clients
        services.AddSingleton<GrpcTlsChannelFactory>();

        // Custom authorization failure handler: returns RFC 7807 problem+json instead of bare 403
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemDetailsAuthorizationHandler>();

        return services;
    }

    /// <summary>
    /// Adds authentication and authorization middleware to the pipeline.
    /// Must be called after UseCors() and before UseRateLimiter().
    /// </summary>
    public static WebApplication UseMultiIdentitySecurity(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }
}
