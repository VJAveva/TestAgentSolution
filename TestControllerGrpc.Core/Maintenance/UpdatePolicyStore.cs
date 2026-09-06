namespace TestControllerGrpc.Core.Maintenance;

/// <summary>Holds the active <see cref="UpdatePolicy"/>. Mutable so the settings UI / policy API can change it
/// without an app restart; <see cref="Changed"/> lets agents be re-pushed the timing knobs (spec §24/R24).</summary>
public interface IUpdatePolicyStore
{
    UpdatePolicy Current { get; }
    void Update(UpdatePolicy policy);
    event EventHandler<UpdatePolicy>? Changed;
}

/// <summary>In-memory <see cref="IUpdatePolicyStore"/>, seeded from configuration at startup.</summary>
public sealed class UpdatePolicyStore : IUpdatePolicyStore
{
    private volatile UpdatePolicy _current;

    public UpdatePolicyStore(UpdatePolicy? initial = null) => _current = initial ?? new UpdatePolicy();

    public UpdatePolicy Current => _current;

    public event EventHandler<UpdatePolicy>? Changed;

    public void Update(UpdatePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _current = policy;
        Changed?.Invoke(this, policy);
    }
}
