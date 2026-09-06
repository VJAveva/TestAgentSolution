namespace TestControllerGrpc.Core.Maintenance;

// Windows Update data shapes (Part 2, spec §16/§19). The agent always sends the complete level state, so the
// controller can reconstruct a node's posture from a single message even after a dropped connection.

/// <summary>One Windows Update, as reported by the agent.</summary>
public sealed record UpdateItemDto
{
    public required string KbId { get; init; }
    public string Title { get; init; } = "";
    /// <summary>Installed | Failed | Pending.</summary>
    public string Result { get; init; } = "";
    /// <summary>e.g. "0x80073712"; empty on success.</summary>
    public string ResultCode { get; init; } = "";
}

/// <summary>The full level state of a node's Windows Update posture (never a delta).</summary>
public sealed record WindowsUpdateStatusDto
{
    public bool RebootRequired { get; init; }
    public int PendingCount { get; init; }
    public IReadOnlyList<UpdateItemDto> Items { get; init; } = [];
    public DateTimeOffset? LastInstallUtc { get; init; }
}

/// <summary>Controller-side twin of the agent's NodeMaintenanceEvent proto (the gRPC handler maps proto → this).</summary>
public sealed record NodeMaintenanceEventDto
{
    public required string NodeId { get; init; }
    public required MaintenanceEventKind Kind { get; init; }
    public required MaintenanceEventSource Source { get; init; }
    public required WindowsUpdateStatusDto Status { get; init; }
    public DateTimeOffset DetectedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? AgentVersion { get; init; }
}

/// <summary>Live per-node update posture held by the controller (in memory; rebuilt from startup snapshots).</summary>
public sealed record NodeUpdateStatus
{
    public required string NodeId { get; init; }
    public required WindowsUpdateState State { get; init; }
    public required MaintenanceEventSource LastSource { get; init; }
    public DateTimeOffset? LastEventUtc { get; init; }

    /// <summary>Wall-clock of the last report from this node — drives staleness display.</summary>
    public DateTimeOffset LastReportUtc { get; init; }

    public DateTimeOffset? LastInstallUtc { get; init; }
    public int PendingCount { get; init; }
    public IReadOnlyList<UpdateItemDto> Items { get; init; } = [];
    public DateTimeOffset? SuppressedUntilUtc { get; init; }
    public DateTimeOffset? SnoozedUntilUtc { get; init; }
    public bool Acknowledged { get; init; }
}

/// <summary>Raised when a node's <see cref="WindowsUpdateState"/> transitions.</summary>
public sealed record NodeUpdateStatusChanged(string NodeId, WindowsUpdateState Previous, WindowsUpdateState Current);

/// <summary>Configurable mapping from update posture to scheduling decision, plus timing knobs (spec §14/§24).</summary>
public sealed record UpdatePolicy
{
    public MaintenanceState PendingEffect { get; init; } = MaintenanceState.None;
    public MaintenanceState InstallingEffect { get; init; } = MaintenanceState.Updating;
    public MaintenanceState RebootRequiredEffect { get; init; } = MaintenanceState.Draining;

    /// <summary>Unattended reboot on RebootRequired — off by default (a warm box's config must not vanish silently).</summary>
    public bool AutoReboot { get; init; }

    /// <summary>Optional daily window during which <see cref="AutoReboot"/> may fire. Null means any time.</summary>
    public TimeOnly? AutoRebootWindowStart { get; init; }

    public TimeOnly? AutoRebootWindowEnd { get; init; }

    public TimeSpan RegistryPollInterval { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan SuppressionWindow { get; init; } = TimeSpan.FromMinutes(60);
    public TimeSpan CoalescingWindow { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxConcurrentReboots { get; init; } = 1;

    /// <summary>True when <paramref name="at"/> falls inside the configured window (or no window is set).
    /// A window whose end is before its start wraps past midnight.</summary>
    public bool IsWithinAutoRebootWindow(DateTimeOffset at)
    {
        if (AutoRebootWindowStart is not { } start || AutoRebootWindowEnd is not { } end)
            return true;

        var now = TimeOnly.FromDateTime(at.LocalDateTime);
        return start <= end
            ? now >= start && now <= end
            : now >= start || now <= end;
    }
}

/// <summary>An operator-facing notification about a node's update posture.</summary>
public sealed record FleetNotification
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string NodeId { get; init; }
    public required MaintenanceEventKind Kind { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    public MaintenanceEventSource Source { get; init; }
    public DateTimeOffset DetectedUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool Acknowledged { get; init; }

    /// <summary>True when raised inside the post-revert suppression window (shown but not alarmed).</summary>
    public bool IsSuppressed { get; init; }
}
