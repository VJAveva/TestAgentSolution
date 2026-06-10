namespace TestControllerGrpc.Identity;

/// <summary>
/// Application roles per 01_System_Design.md §3.1.
/// Stored as INTEGER in the Users table.
/// </summary>
public enum Role
{
    Administrator = 0,
    SeniorManager = 1,
    Engineer = 2,
    Guest = 3
}
