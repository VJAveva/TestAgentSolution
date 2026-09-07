namespace TestController.Api.Security;

/// <summary>
/// Resolves a caller's capability names from the primary host.
/// </summary>
/// <remarks>
/// Registered only by hosts that proxy identity (the standalone WebApi). Their local <c>ISessionStore</c> is a
/// no-op, so a bearer token can never be resolved locally — but they still serve permission-gated endpoints of
/// their own, which must stay gated rather than fail open or fail closed.
/// </remarks>
public interface IRemoteCapabilityResolver
{
    /// <summary>Capability names for the caller, or null when the primary host cannot identify them.</summary>
    Task<IReadOnlySet<string>?> GetCapabilitiesAsync(string? authorizationHeader, CancellationToken ct);
}
