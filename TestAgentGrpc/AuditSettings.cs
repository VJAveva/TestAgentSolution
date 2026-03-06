namespace TestAgentGrpc;

/// <summary>
/// Strongly-typed settings from appsettings.json ? "AuditSettings".
/// </summary>
public sealed class AuditSettings
{
    public bool Enabled { get; set; } = true;
    public string LogDirectory { get; set; } = @"C:\TestAgentSolution\Logs\audit";
    public int RetentionDays { get; set; } = 30;
    public int MaxFileSizeMb { get; set; } = 50;
    public int LogHeartbeatEveryN { get; set; } = 10;
}
