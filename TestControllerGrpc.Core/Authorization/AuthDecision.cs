namespace TestControllerGrpc.Authorization;

/// <summary>
/// Result of an authorization check. Every call to IAuthorizationService.CanAsync
/// produces one of these — both allows and denies are audited.
/// Per 01_System_Design.md §3.2.
/// </summary>
public sealed record AuthDecision
{
    public bool Allowed { get; }
    public string ReasonCode { get; }
    public string? HumanReadable { get; }

    private AuthDecision(bool allowed, string reasonCode, string? humanReadable = null)
    {
        Allowed = allowed;
        ReasonCode = reasonCode;
        HumanReadable = humanReadable;
    }

    public static AuthDecision Allow(string reasonCode) => new(true, reasonCode);
    public static AuthDecision Deny(string reasonCode, string? humanReadable = null) => new(false, reasonCode, humanReadable);
}
