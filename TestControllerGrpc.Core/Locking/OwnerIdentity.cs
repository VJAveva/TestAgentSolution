using TestControllerGrpc.Identity;

namespace TestControllerGrpc.Locking;

/// <summary>
/// Identifies who holds a pipeline lock.
/// Equality is on UserId ONLY — same-owner re-acquire semantics.
/// Per 03_Integration_With_Lock_Spec.md §4.3 invariant 1.
/// </summary>
public sealed record OwnerIdentity(string UserId, string DisplayName, ClientKind ClientKind)
{
    public bool Equals(OwnerIdentity? other) => other is not null && UserId == other.UserId;
    public override int GetHashCode() => UserId.GetHashCode();
}
