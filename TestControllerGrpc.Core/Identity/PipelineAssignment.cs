namespace TestControllerGrpc.Identity;

/// <summary>
/// POCO entity for the PipelineAssignments table.
/// Links an Engineer to a specific pipeline they can operate on.
/// Per 01_System_Design.md §7.1.
/// </summary>
public sealed class PipelineAssignment
{
    public string UserId { get; set; } = "";
    public string PipelineId { get; set; } = "";
    public DateTime AssignedUtc { get; set; } = DateTime.UtcNow;
    public string AssignedByUserId { get; set; } = "";
}
