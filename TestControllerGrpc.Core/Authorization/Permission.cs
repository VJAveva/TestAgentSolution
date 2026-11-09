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
    Notification_Mute,
    System_ChangeMode,

    // Appended, never reordered: audit rows persist this enum.
    CodeChurn_View,
    CodeChurn_Export,
    ReportCard_View,

    /// <summary>Install Windows updates on a fleet node. Administrator only - it patches a live machine.</summary>
    Fleet_InstallUpdates,

    /// <summary>
    /// Author WatchList content - create a pipeline with the Pipeline Builder. Administrator only:
    /// authoring decides which commands run on which agents, so it is strictly more powerful than
    /// triggering a pipeline somebody else authored.
    /// </summary>
    Pipeline_Author,

    /// <summary>Turn per-team features on or off (Features.json). Administrator only.</summary>
    System_ConfigureFeatures,

    /// <summary>Add, change, move or delete nodes inside one pipeline. Scoped by pipeline assignment.</summary>
    Pipeline_Edit,

    /// <summary>Change a Library template. Administrator only: a template change affects every pipeline that uses it.</summary>
    Template_Edit
}
