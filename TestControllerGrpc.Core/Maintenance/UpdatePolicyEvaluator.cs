namespace TestControllerGrpc.Core.Maintenance;

/// <summary>
/// Maps a node's Windows Update posture to a scheduling decision, per the configured policy (spec §14/§16).
/// Defaults: pending → None (notify only), installing → Updating, reboot-required → Draining; everything else → None.
/// </summary>
public sealed class UpdatePolicyEvaluator : IUpdatePolicyEvaluator
{
    public MaintenanceState Evaluate(NodeUpdateStatus status, UpdatePolicy policy) => status.State switch
    {
        WindowsUpdateState.UpdatePending => policy.PendingEffect,
        WindowsUpdateState.UpdateInstalling => policy.InstallingEffect,
        WindowsUpdateState.RebootRequired => policy.RebootRequiredEffect,
        _ => MaintenanceState.None,
    };
}
