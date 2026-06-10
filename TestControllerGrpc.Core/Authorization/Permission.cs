namespace TestControllerGrpc.Authorization;

/// <summary>
/// Full permission catalog per 01_System_Design.md §3.1.
/// Named Resource_Action. Every gRPC handler names exactly one permission.
/// </summary>
public enum Permission
{
    Pipeline_View,
    Pipeline_Trigger,
    Pipeline_Cancel,
    Pipeline_Retry,
    Pipeline_TriggerAll,
    Pipeline_CancelAll,
    Pipeline_Enable,
    Pipeline_Disable,
    Pipeline_ForceRelease,
    User_Create,
    User_Update,
    User_Delete,
    User_Assign,
    User_Revoke,
    Report_View,
    Report_Generate,
    Audit_View,
    Audit_Export,
    Notification_Mute
}
