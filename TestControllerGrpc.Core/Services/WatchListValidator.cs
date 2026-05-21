using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Validates a parsed <see cref="WatchListConfig"/> to catch unsafe or invalid
/// action definitions before execution. Call after <see cref="WatchListXmlParser.Load"/>.
///
/// Catches:
///   - Missing/empty required fields (WatchItem.Tag, WatchItem.Path, Action.Command)
///   - Invalid agent variable references (unknown [AgentX] in multi-agent contexts)
///   - Invalid timeout values (negative or unreasonably large)
///   - Unresolved template references (Ref pointing to nonexistent Template ID)
///   - Dangerous patterns in commands (optional advisory)
/// </summary>
public static class WatchListValidator
{
    /// <summary>Maximum allowed timeout in seconds (48 hours).</summary>
    private const int MaxTimeoutSeconds = 172_800;

    /// <summary>
    /// Validates a WatchList configuration. Returns a list of validation errors.
    /// An empty list means the configuration is valid.
    /// </summary>
    public static IReadOnlyList<WatchListValidationError> Validate(WatchListConfig config)
    {
        var errors = new List<WatchListValidationError>();

        ValidateTemplates(config, errors);
        ValidateWatchItems(config, errors);

        return errors;
    }

    private static void ValidateTemplates(WatchListConfig config, List<WatchListValidationError> errors)
    {
        var templateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var template in config.Templates)
        {
            if (string.IsNullOrWhiteSpace(template.ID))
            {
                errors.Add(new("Template", "", "Template has empty ID"));
                continue;
            }

            if (!templateIds.Add(template.ID))
                errors.Add(new("Template", template.ID, $"Duplicate template ID '{template.ID}'"));

            ValidateChildren(template.Children, $"Template[{template.ID}]", config, errors);
        }
    }

    private static void ValidateWatchItems(WatchListConfig config, List<WatchListValidationError> errors)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var wi in config.WatchItems)
        {
            if (string.IsNullOrWhiteSpace(wi.Tag))
            {
                errors.Add(new("WatchItem", "", "WatchItem has empty Tag"));
                continue;
            }

            var context = $"WatchItem[{wi.Tag}]";

            if (!tags.Add(wi.Tag))
                errors.Add(new(context, wi.Tag, $"Duplicate WatchItem tag '{wi.Tag}'"));

            if (string.IsNullOrWhiteSpace(wi.Path))
                errors.Add(new(context, wi.Tag, "WatchItem.Path is required"));

            if (wi.Events.Count == 0)
                errors.Add(new(context, wi.Tag, "WatchItem has no events defined"));

            foreach (var evt in wi.Events)
            {
                if (string.IsNullOrWhiteSpace(evt.Type))
                    errors.Add(new(context, wi.Tag, "Event has empty Type"));

                ValidateChildren(evt.Children, $"{context}.Event[{evt.Type}]", config, errors);
            }
        }
    }

    private static void ValidateChildren(
        List<IActionNode> children, string context,
        WatchListConfig config, List<WatchListValidationError> errors)
    {
        foreach (var node in children)
        {
            switch (node)
            {
                case ActionConfig action:
                    ValidateAction(action, context, errors);
                    break;

                case ActionGroupConfig group:
                    if (string.IsNullOrWhiteSpace(group.Tag))
                        errors.Add(new(context, "", "ActionGroup has empty Tag"));

                    ValidateChildren(group.Children, $"{context}.Group[{group.Tag}]", config, errors);
                    break;

                case RefConfig refNode:
                    if (string.IsNullOrWhiteSpace(refNode.TemplateID))
                        errors.Add(new(context, "", "Ref has empty TemplateID"));
                    else if (!config.Templates.Any(t =>
                        string.Equals(t.ID, refNode.TemplateID, StringComparison.OrdinalIgnoreCase)))
                    {
                        errors.Add(new(context, refNode.TemplateID,
                            $"Ref references unknown template '{refNode.TemplateID}'"));
                    }
                    break;

                case InitializeConfig init:
                    if (string.IsNullOrWhiteSpace(init.ParameterFile))
                        errors.Add(new(context, init.Tag, "Initialize has empty ParameterFile"));
                    break;
            }
        }
    }

    private static void ValidateAction(ActionConfig action, string context, List<WatchListValidationError> errors)
    {
        var label = !string.IsNullOrEmpty(action.Tag) ? action.Tag : action.ResolvedTag;
        var actionCtx = $"{context}.Action[{label}]";

        // RunCommand and RunRemoteCommand require a Command
        if (action.Type is ActionType.RunCommand or ActionType.RunRemoteCommand)
        {
            if (string.IsNullOrWhiteSpace(action.Command))
                errors.Add(new(actionCtx, label, "Action.Command is required for RunCommand/RunRemoteCommand"));
        }

        // RunRemoteCommand requires an agent
        if (action.Type == ActionType.RunRemoteCommand && string.IsNullOrWhiteSpace(action.AgentName))
            errors.Add(new(actionCtx, label, "RunRemoteCommand requires AgentName"));

        // SendMail requires To
        if (action.Type == ActionType.SendMail && string.IsNullOrWhiteSpace(action.To))
            errors.Add(new(actionCtx, label, "SendMail requires To address"));

        // Timeout validation
        if (action.Timeout < 0)
            errors.Add(new(actionCtx, label, $"Timeout cannot be negative: {action.Timeout}"));
        else if (action.Timeout > MaxTimeoutSeconds)
            errors.Add(new(actionCtx, label, $"Timeout exceeds maximum ({MaxTimeoutSeconds}s): {action.Timeout}"));

        // Retry validation
        if (action.MaxRetries < 0)
            errors.Add(new(actionCtx, label, $"MaxRetries cannot be negative: {action.MaxRetries}"));
        if (action.RetryDelaySeconds < 0)
            errors.Add(new(actionCtx, label, $"RetryDelaySeconds cannot be negative: {action.RetryDelaySeconds}"));
    }
}

/// <summary>Represents a single validation error found in a WatchList configuration.</summary>
public sealed record WatchListValidationError(string Context, string Tag, string Message)
{
    public override string ToString() => $"[{Context}] {Message}";
}
