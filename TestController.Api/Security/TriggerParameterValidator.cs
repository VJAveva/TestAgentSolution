using TestController.Api.Controllers;

namespace TestController.Api.Security;

/// <summary>
/// Validates caller-supplied trigger parameters at the HTTP boundary.
/// </summary>
/// <remarks>
/// Trigger parameters are substituted verbatim by <c>ParameterResolver.Resolve</c> into an action's
/// <c>Command</c> and <c>Parameters</c>, which reach the shell as a single raw argument string
/// (<c>cmd.exe /c "script.bat" {args}</c> / <c>powershell.exe -File "script.ps1" {args}</c>). A metacharacter
/// therefore escapes the intended command, turning the comparatively weak <c>Pipeline_Trigger</c> permission
/// into arbitrary code execution on the controller. CR/LF additionally forge extra <c>Key,Value</c> lines in
/// the parameter file the trigger persists, poisoning later runs.
///
/// Rejecting at the boundary is deliberate: the correct escaping differs per interpreter (cmd vs PowerShell
/// vs direct exec), so there is no single downstream encode that is right in all three cases.
/// </remarks>
internal static class TriggerParameterValidator
{
    private const int MaxKeyLength = 64;
    private const int MaxValueLength = 2048;
    private const int MaxParameterCount = 128;

    // Shell metacharacters for cmd.exe and PowerShell. Deliberately does NOT include characters that occur in
    // legitimate values: '\' '/' ':' (UNC drop paths), ',' (email lists), '@', '.', '-', '(', ')', '%'.
    // Bare '$' is allowed because UNC admin shares use it (\\jvgr1\C$\TestAgentService); only the PowerShell
    // subexpression forms below turn '$' into execution. Bare '\'' is allowed - it cannot execute anything once
    // the separators and substitution forms are blocked, and it appears in real folder names.
    private static readonly char[] ShellMetacharacters = ['&', '|', ';', '<', '>', '^', '`', '"'];

    // Substitution forms that DO execute: PowerShell subexpression and array-subexpression operators.
    private static readonly string[] SubstitutionSequences = ["$(", "@("];

    /// <summary>Returns null when the request is acceptable, otherwise a caller-facing rejection reason.</summary>
    internal static string? Validate(TriggerRequest? request)
    {
        if (request is null) return null;

        if (Describe("BuildNumber", request.BuildNumber) is { } buildError) return buildError;
        if (Describe("DropLocation", request.DropLocation) is { } dropError) return dropError;

        if (request.Parameters is not { } parameters) return null;

        if (parameters.Count > MaxParameterCount)
            return $"Too many parameters ({parameters.Count}); the limit is {MaxParameterCount}.";

        foreach (var (key, value) in parameters)
        {
            if (!IsValidKey(key))
                return $"Parameter name '{Sanitize(key)}' is invalid; use letters, digits and underscore only (max {MaxKeyLength} characters).";

            if (Describe(key, value) is { } valueError) return valueError;
        }

        return null;
    }

    private static bool IsValidKey(string? key) =>
        !string.IsNullOrEmpty(key)
        && key.Length <= MaxKeyLength
        && key.All(c => c == '_' || char.IsAsciiLetterOrDigit(c));

    private static string? Describe(string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;

        if (value.Length > MaxValueLength)
            return $"Parameter '{Sanitize(key)}' is {value.Length} characters; the limit is {MaxValueLength}.";

        var index = value.IndexOfAny(ShellMetacharacters);
        if (index >= 0)
            return $"Parameter '{Sanitize(key)}' contains the disallowed character '{value[index]}'. " +
                   "Shell metacharacters are rejected because parameter values are passed to a command line.";

        foreach (var sequence in SubstitutionSequences)
        {
            if (value.Contains(sequence, StringComparison.Ordinal))
                return $"Parameter '{Sanitize(key)}' contains the disallowed sequence '{sequence}'. " +
                       "Shell substitution is rejected because parameter values are passed to a command line.";
        }

        foreach (var c in value)
        {
            if (char.IsControl(c))
                return $"Parameter '{Sanitize(key)}' contains a control character (U+{(int)c:X4}); line breaks and control characters are not allowed.";
        }

        return null;
    }

    // The key is echoed back to the caller, so strip anything that could corrupt the error payload or a log line.
    private static string Sanitize(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "(empty)";
        var trimmed = key.Length > MaxKeyLength ? key[..MaxKeyLength] : key;
        return new string(trimmed.Select(c => char.IsControl(c) ? '?' : c).ToArray());
    }
}
