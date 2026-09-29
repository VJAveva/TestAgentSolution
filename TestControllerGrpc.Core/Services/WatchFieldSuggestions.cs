using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Supplies the valid/suggested values for each editable WatchItem field so the
/// builder UI can bind dropdowns and auto-complete to known-good values instead
/// of free-form text. This is the "auto-selection recommendation" layer of the
/// WatchItem Builder (spec Phase 4) and underpins the "no room for compilation
/// issues" guarantee: the form only offers values the schema accepts.
///
/// All values are plugged from the real schema (see <see cref="WatchListConfig"/>,
/// <see cref="ActionType"/>, <see cref="ExecutionMode"/>) and the real token
/// resolution rules (see <c>ParameterResolver</c>: tokens resolve both with and
/// without a leading underscore, so <c>[BuildNumber]</c> and <c>[_BuildNumber]</c>
/// are equivalent).
/// </summary>
public sealed class WatchFieldSuggestions
{
    /// <summary>Event trigger types supported by the file watcher.</summary>
    public IReadOnlyList<string> EventTypes { get; } =
        new[] { "Renamed", "Created", "Changed" };

    /// <summary>Execution modes for Event / ActionGroup nodes.</summary>
    public IReadOnlyList<string> ExecutionTypes { get; } =
        Enum.GetNames<ExecutionMode>();

    /// <summary>The real action types from the <see cref="ActionType"/> enum.</summary>
    public IReadOnlyList<string> ActionTypes { get; } =
        Enum.GetNames<ActionType>();

    /// <summary>
    /// Live agent names. Defaults empty; the host injects the controller's
    /// registered agents (see <c>IAgentGrpcDispatcher.RegisteredAgents</c>) so the
    /// dropdown reflects reality, not a stale list.
    /// </summary>
    public IReadOnlyList<string> AgentNames { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Common substitution tokens. Both bare and underscore-prefixed forms resolve
    /// (per ParameterResolver), so the bare forms are offered as the primary
    /// suggestions and the underscore forms as the raw trigger-file keys.
    /// </summary>
    public IReadOnlyList<string> AvailableTokens { get; } = new[]
    {
        "[BuildNumber]", "[DropLocation]", "[ControllerName]",
        "[_BuildNumber]", "[_DropLocation]", "[_ControllerName]",
    };

    /// <summary>
    /// Which attributes are valid for a given action type. The form shows ONLY
    /// these fields, preventing invalid attribute combinations. Mirrors what
    /// <c>WatchListXmlParser</c> reads/writes for each type.
    /// </summary>
    public IReadOnlyList<string> AttributesFor(ActionType type) => type switch
    {
        ActionType.RunRemoteCommand => new[]
        {
            "AgentName", "Command", "Parameters", "Timeout", "PollInterval",
            "FailAndContinue", "IsReboot", "Tag", "UserName", "Password",
            "CompletionCheckCommand", "CompletionPollIntervalSeconds",
            "MaxRetries", "RetryDelaySeconds", "RetryBackoff", "RetryOnExitCodes",
        },
        ActionType.RunCommand => new[]
        {
            "Command", "Parameters", "Timeout", "FailAndContinue", "Tag",
            "CompletionCheckCommand", "CompletionPollIntervalSeconds",
            "MaxRetries", "RetryDelaySeconds", "RetryBackoff", "RetryOnExitCodes",
        },
        ActionType.SendMail => new[]
        {
            "Order", "From", "To", "Title", "Body",
            "Attachment", "Embed", "LargeFilesShare", "Tag",
        },
        _ => Array.Empty<string>(),
    };

    /// <summary>Default From address for new SendMail actions (controller appsettings).</summary>
    public string DefaultMailFrom { get; set; } = "wwapps@magellandev2000.dev.wonderware.com";

    /// <summary>Timeout (seconds) for an action detected as an install.</summary>
    public const int InstallTimeoutSeconds = 3600;

    /// <summary>Timeout (seconds) for every other command action.</summary>
    public const int DefaultTimeoutSeconds = 600;

    /// <summary>Poll interval in MILLISECONDS (60 s). Timeout is in seconds - the units differ.</summary>
    public const int DefaultPollIntervalMs = 60_000;

    /// <summary>
    /// True when the action looks like a build install. Parameters is checked too: installs are
    /// normally "cmd" with the actual script in Parameters, so Command alone misses them.
    /// </summary>
    public static bool LooksLikeInstall(string? tag, string? command, string? parameters) =>
        Mentions(tag) || Mentions(command) || Mentions(parameters);

    private static bool Mentions(string? value) =>
        value is not null && value.Contains("install", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Sensible defaults when a new action node is created. Note PollInterval is
    /// in milliseconds and Timeout is in seconds.
    /// </summary>
    public ActionConfig NewActionDefaults(ActionType type) => type switch
    {
        ActionType.RunRemoteCommand => new ActionConfig
        {
            Type = type,
            Command = "cmd",
            Timeout = DefaultTimeoutSeconds,
            PollInterval = DefaultPollIntervalMs,
            FailAndContinue = false,
        },
        ActionType.RunCommand => new ActionConfig
        {
            Type = type,
            Command = "cmd",
            Timeout = DefaultTimeoutSeconds,
            PollInterval = DefaultPollIntervalMs,
            FailAndContinue = true,
        },
        ActionType.SendMail => new ActionConfig
        {
            Type = type,
            From = DefaultMailFrom,
        },
        _ => new ActionConfig { Type = type },
    };

    /// <summary>
    /// Re-applies the timeout default once the action's text is known, so an install gets the
    /// long timeout. Only touches an action still sitting on the non-install default.
    /// </summary>
    public static void ApplyInstallTimeoutIfDetected(ActionConfig action)
    {
        if (action.Type is not (ActionType.RunCommand or ActionType.RunRemoteCommand)) return;
        if (action.Timeout != DefaultTimeoutSeconds) return;
        if (LooksLikeInstall(action.Tag, action.Command, action.Parameters))
            action.Timeout = InstallTimeoutSeconds;
    }
}
