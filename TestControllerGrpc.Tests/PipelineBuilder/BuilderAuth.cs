using TestControllerGrpc.Authorization;
using TestControllerGrpc.Identity;

namespace TestControllerGrpc.Tests.PipelineBuilder;

/// <summary>Authorization doubles shared by the builder test classes.</summary>
internal static class BuilderAuth
{
    public static IUserContext Admin { get; } = new Caller(nameof(Role.Administrator));

    public static IUserContext Engineer { get; } = new Caller(nameof(Role.Engineer));

    public static IAuthorizationService AllowAll { get; } = new Decider(_ => true);

    /// <summary>Mirrors the real rule for this permission: Administrator only.</summary>
    public static IAuthorizationService AdminOnly { get; } =
        new Decider(user => string.Equals(
            user.Roles.FirstOrDefault(), nameof(Role.Administrator), StringComparison.OrdinalIgnoreCase));

    private sealed class Caller(string role) : IUserContext
    {
        public string UserId => $"{role}-id";
        public string DisplayName => role;
        public IReadOnlyList<string> Roles => [role];
        public IReadOnlySet<string> AssignedPipelineIds { get; } = new HashSet<string>();
        public ClientKind ClientKind => ClientKind.Wpf;
        public string? GuestId => null;
    }

    private sealed class Decider(Func<IUserContext, bool> allow) : IAuthorizationService
    {
        public Task<AuthDecision> CanAsync(
            IUserContext user, Permission permission, string? resourceId = null, CancellationToken ct = default) =>
            Task.FromResult(allow(user)
                ? AuthDecision.Allow("test-allow")
                : AuthDecision.Deny("test-deny", "You do not have permission to author pipelines."));
    }
}
