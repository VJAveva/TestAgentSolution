namespace TestControllerGrpc.Ado;

/// <summary>Provides bearer tokens/auth headers for Azure DevOps REST calls.</summary>
public interface IAdoTokenProvider
{
    /// <summary>Returns a ready-to-use "Authorization" header value (e.g. "Bearer xyz" or "Basic xyz").</summary>
    Task<string> GetAuthHeaderAsync(CancellationToken ct);

    /// <summary>Human-readable description of the credential source, for logging/diagnostics — never the secret itself.</summary>
    string Describe();
}

/// <summary>Thrown when the configured credential (PAT, cert, secret) cannot be located.</summary>
public sealed class AdoCredentialMissingException : Exception
{
    public AdoCredentialMissingException(string message) : base(message) { }
    public AdoCredentialMissingException(string message, Exception inner) : base(message, inner) { }
}
