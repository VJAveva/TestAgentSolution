namespace TestAgentGrpc;

/// <summary>
/// Strongly-typed settings from appsettings.json ? "NotificationSettings".
/// </summary>
public sealed class NotificationSettings
{
    public bool NotifyOnControllerLost { get; set; } = true;
    public bool NotifyOnControllerRecovered { get; set; } = true;
    public bool NotifyOnCommandReceived { get; set; } = false;
    public bool NotifyOnCommandCompleted { get; set; } = false;
}
