using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TestController.Api.Security;
using TestController.Api.Services;
using TestControllerGrpc.Core.Maintenance;

namespace TestController.Api.Controllers;

/// <summary>
/// REST surface for fleet maintenance (spec Prompt 12). All requests here are tagged
/// <see cref="MaintenanceTriggerSource.WebApi"/>. The maintenance engine runs on the controller host, so on a host
/// without it (the standalone proxy) every action returns 503 rather than a null-reference 500.
/// </summary>
[ApiController]
[Route("api/maintenance")]
[Authorize(Policy = SecurityPolicies.User)]
public class MaintenanceController : ControllerBase
{
    private readonly IFleetMaintenanceService? _service;
    private readonly IMaintenanceOperationStore? _store;
    private readonly INodeUpdateStatusStore? _updates;
    private readonly IFleetNotificationService? _notifications;
    private readonly IUpdatePolicyStore? _policy;
    private readonly IMaintenanceStateStore? _stateStore;

    public MaintenanceController(
        IFleetMaintenanceService? service = null,
        IMaintenanceOperationStore? store = null,
        INodeUpdateStatusStore? updates = null,
        IFleetNotificationService? notifications = null,
        IUpdatePolicyStore? policy = null,
        IMaintenanceStateStore? stateStore = null)
    {
        _service = service;
        _store = store;
        _updates = updates;
        _notifications = notifications;
        _policy = policy;
        _stateStore = stateStore;
    }

    public sealed record RevertApiRequest(
        string NodeId, string SnapshotName, bool? WaitForAgent, bool? RunPrep, bool? InstallBuild, bool? ForceIfBusy, string? Reason);

    public sealed record RebootApiRequest(string NodeId, int? RebootDelaySeconds, bool? ForceIfBusy, string? Reason);

    public sealed record PrecheckApiRequest(IReadOnlyList<string> NodeIds);

    public sealed record SnoozeApiRequest(int? Hours);

    public sealed record UpdatePolicyApiRequest(
        string? PendingEffect,
        string? InstallingEffect,
        string? RebootRequiredEffect,
        bool? AutoReboot,
        string? AutoRebootWindowStart,
        string? AutoRebootWindowEnd,
        int? RegistryPollSeconds,
        int? SuppressionWindowSeconds,
        int? CoalescingWindowSeconds,
        int? MaxConcurrentReboots);

    [HttpPost("revert")]
    public async Task<IActionResult> Revert([FromBody] RevertApiRequest req, CancellationToken ct)
    {
        if (_service is null) return MaintenanceUnavailable();

        var request = new RevertRequest
        {
            NodeId = req.NodeId,
            SnapshotName = req.SnapshotName,
            WaitForAgent = req.WaitForAgent ?? true,
            RunPrep = req.RunPrep ?? true,
            InstallBuild = req.InstallBuild ?? false,
            ForceIfBusy = req.ForceIfBusy ?? false,
            TriggerSource = MaintenanceTriggerSource.WebApi,
            TriggeredBy = User.Identity?.Name,
            Reason = req.Reason,
        };

        try
        {
            var id = await _service.StartRevertAsync(request, ct);
            return Ok(new { operationId = id });
        }
        catch (MaintenanceInProgressException ex)
        {
            return Conflict(new { error = "maintenance-in-progress", nodeId = ex.NodeId, existingOperationId = ex.ExistingOperationId });
        }
    }

    [HttpPost("reboot")]
    public async Task<IActionResult> Reboot([FromBody] RebootApiRequest req, CancellationToken ct)
    {
        if (_service is null) return MaintenanceUnavailable();

        var request = new RebootRequest
        {
            NodeId = req.NodeId,
            RebootDelaySeconds = req.RebootDelaySeconds ?? 5,
            ForceIfBusy = req.ForceIfBusy ?? false,
            TriggerSource = MaintenanceTriggerSource.WebApi,
            TriggeredBy = User.Identity?.Name,
            Reason = req.Reason,
        };

        try
        {
            var id = await _service.StartRebootAsync(request, ct);
            return Ok(new { operationId = id });
        }
        catch (MaintenanceInProgressException ex)
        {
            return Conflict(new { error = "maintenance-in-progress", nodeId = ex.NodeId, existingOperationId = ex.ExistingOperationId });
        }
    }

    [HttpPost("precheck")]
    public async Task<IActionResult> Precheck([FromBody] PrecheckApiRequest req, CancellationToken ct)
    {
        if (_service is null) return MaintenanceUnavailable();
        var result = await _service.PrecheckAsync(req.NodeIds, ct);
        return Ok(result);
    }

    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> Cancel(Guid id)
    {
        if (_service is null) return MaintenanceUnavailable();
        return await _service.RequestCancelAsync(id) ? Ok() : NotFound();
    }

    [HttpPost("nodes/{nodeId}/clear-quarantine")]
    public async Task<IActionResult> ClearQuarantine(string nodeId)
    {
        if (_service is null) return MaintenanceUnavailable();
        await _service.ClearQuarantineAsync(nodeId, User.Identity?.Name ?? "web");
        return Ok();
    }

    [HttpGet("active")]
    public IActionResult Active()
    {
        if (_service is null) return MaintenanceUnavailable();
        return Ok(_service.ActiveOperations);
    }

    [HttpGet("history")]
    public async Task<IActionResult> History(
        [FromQuery] string? nodeId, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, CancellationToken ct)
    {
        if (_store is null) return MaintenanceUnavailable();
        var rows = await _store.GetHistoryAsync(nodeId, from, to, ct);
        return Ok(rows);
    }

    // ── Windows Update posture (spec R24) ────────────────────────────

    [HttpGet("updates")]
    public IActionResult Updates()
    {
        if (_updates is null) return MaintenanceUnavailable();
        return Ok(_updates.GetAll().Select(FleetUpdateDto.From));
    }

    [HttpGet("updates/{nodeId}")]
    public IActionResult UpdatesForNode(string nodeId)
    {
        if (_updates is null) return MaintenanceUnavailable();
        var status = _updates.Get(nodeId);
        return status is null ? NotFound() : Ok(FleetUpdateDto.From(status));
    }

    [HttpPost("updates/{nodeId}/snooze")]
    public IActionResult SnoozeUpdates(string nodeId, [FromBody] SnoozeApiRequest? req)
    {
        if (_notifications is null) return MaintenanceUnavailable();
        var hours = req?.Hours is > 0 and <= 168 ? req.Hours!.Value : 4;
        _notifications.Snooze(nodeId, TimeSpan.FromHours(hours));
        return Ok(new { nodeId, snoozedForHours = hours });
    }

    [HttpPost("updates/{nodeId}/acknowledge")]
    public IActionResult AcknowledgeUpdates(string nodeId)
    {
        if (_notifications is null) return MaintenanceUnavailable();

        var open = _notifications.Notifications
            .Where(n => string.Equals(n.NodeId, nodeId, StringComparison.OrdinalIgnoreCase) && !n.Acknowledged)
            .ToList();
        foreach (var note in open)
            _notifications.Acknowledge(note.Id);

        return Ok(new { nodeId, acknowledged = open.Count });
    }

    /// <summary>Queues a reboot for every node holding a pending reboot that is not still draining a run (R19).</summary>
    [HttpPost("updates/reboot-ready")]
    public async Task<IActionResult> RebootReady(CancellationToken ct)
    {
        if (_service is null || _updates is null) return MaintenanceUnavailable();

        var queued = new List<string>();
        var skipped = new List<string>();

        foreach (var status in _updates.GetAll().Where(s => s.State == WindowsUpdateState.RebootRequired))
        {
            if (_stateStore?.Get(status.NodeId) == MaintenanceState.Draining)
            {
                skipped.Add(status.NodeId);
                continue;
            }

            try
            {
                // Concurrency is capped inside the service, which queues the overflow rather than rejecting it.
                await _service.StartRebootAsync(new RebootRequest
                {
                    NodeId = status.NodeId,
                    TriggerSource = MaintenanceTriggerSource.WebApi,
                    TriggeredBy = User.Identity?.Name,
                    Reason = "Reboot all ready (Windows Update)",
                }, ct);
                queued.Add(status.NodeId);
            }
            catch (MaintenanceInProgressException)
            {
                skipped.Add(status.NodeId);
            }
        }

        return Ok(new { queued, skipped });
    }

    [HttpGet("policy")]
    public IActionResult GetPolicy()
    {
        if (_policy is null) return MaintenanceUnavailable();
        return Ok(FleetUpdateDto.From(_policy.Current));
    }

    [HttpPut("policy")]
    [Authorize(Policy = SecurityPolicies.Admin)]
    public IActionResult PutPolicy([FromBody] UpdatePolicyApiRequest req)
    {
        if (_policy is null) return MaintenanceUnavailable();

        var current = _policy.Current;
        var updated = new UpdatePolicy
        {
            PendingEffect = ParseState(req.PendingEffect, current.PendingEffect),
            InstallingEffect = ParseState(req.InstallingEffect, current.InstallingEffect),
            RebootRequiredEffect = ParseState(req.RebootRequiredEffect, current.RebootRequiredEffect),
            AutoReboot = req.AutoReboot ?? current.AutoReboot,
            AutoRebootWindowStart = ParseTime(req.AutoRebootWindowStart, current.AutoRebootWindowStart),
            AutoRebootWindowEnd = ParseTime(req.AutoRebootWindowEnd, current.AutoRebootWindowEnd),
            RegistryPollInterval = Seconds(req.RegistryPollSeconds, current.RegistryPollInterval),
            SuppressionWindow = Seconds(req.SuppressionWindowSeconds, current.SuppressionWindow),
            CoalescingWindow = Seconds(req.CoalescingWindowSeconds, current.CoalescingWindow),
            MaxConcurrentReboots = req.MaxConcurrentReboots is > 0 ? req.MaxConcurrentReboots.Value : current.MaxConcurrentReboots,
        };

        _policy.Update(updated);
        return Ok(FleetUpdateDto.From(updated));
    }

    private static MaintenanceState ParseState(string? value, MaintenanceState fallback)
        => Enum.TryParse<MaintenanceState>(value, ignoreCase: true, out var parsed) ? parsed : fallback;

    private static TimeOnly? ParseTime(string? value, TimeOnly? fallback)
    {
        if (value is null) return fallback;
        if (value.Length == 0) return null;   // explicit clear
        return TimeOnly.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static TimeSpan Seconds(int? value, TimeSpan fallback)
        => value is > 0 ? TimeSpan.FromSeconds(value.Value) : fallback;

    private IActionResult MaintenanceUnavailable()
        => StatusCode(503, new { error = "maintenance-unavailable", message = "Maintenance runs on the controller host." });
}
