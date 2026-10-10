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

    // ═══════════════════════════════════════════════════════════════════
    // Severity-aware analysis (WatchItem Builder — Phase 3)
    // ───────────────────────────────────────────────────────────────────
    // Analyze() is a superset of Validate(): it surfaces the same blocking
    // errors PLUS advisory warnings (plaintext passwords, absurd timeouts,
    // poll-interval/timeout mismatch, literal build numbers, empty groups).
    // The builder UI uses this to drive the validation panel and the save
    // guard (Save is blocked while any Error-severity issue exists).
    // Validate() is left untouched for callers that only want hard errors.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Advisory timeout ceiling (24h). Above this is almost certainly a typo.</summary>
    private const int SuspiciousTimeoutSeconds = 86_400;

    /// <summary>
    /// Full analysis returning severity-tagged issues. Errors mirror
    /// <see cref="Validate"/>; warnings add the builder's advisory rules.
    /// </summary>
    /// <param name="knownAgents">
    /// Live agent roster. When supplied, a RunRemoteCommand naming an agent outside it is flagged.
    /// Trailing-optional so existing callers keep compiling; null means "roster unknown, do not judge".
    /// </param>
    public static IReadOnlyList<ValidationIssue> Analyze(
        WatchListConfig config, IReadOnlyCollection<string>? knownAgents = null)
    {
        // Reuse the single source of truth for blocking errors, then layer warnings.
        var issues = Validate(config)
            .Select(e => new ValidationIssue(WatchIssueSeverity.Error, e.Context, e.Message))
            .ToList();

        foreach (var wi in config.WatchItems)
            AnalyzeWatchItemWarnings(wi, issues);

        foreach (var wi in config.WatchItems)
            AnalyzeParameterSources(config, wi, issues);

        if (knownAgents is { Count: > 0 })
            AnalyzeAgentNames(config, knownAgents, issues);

        return issues;
    }

    /// <summary>
    /// Checks each WatchItem's Initialize sources against the files on disk: the file exists, the
    /// named Profile is in it, the WatchItem Tag has a pipelines entry, and every [Token] the
    /// actions use is defined by some layer.
    /// </summary>
    /// <remarks>
    /// These are the four failures that used to be completely silent. A renamed Tag simply stopped
    /// matching <c>pipelines[tag]</c> and all its overrides vanished with no signal anywhere; a
    /// mistyped Profile did the same. Reading the files is deliberate - none of it is knowable from
    /// the XML alone, and a validator that cannot open the parameter file cannot catch the bug.
    /// </remarks>
    private static void AnalyzeParameterSources(
        WatchListConfig config, WatchItemConfig wi, List<ValidationIssue> issues)
    {
        var path = $"WatchItem[{wi.Tag}]";
        var inits = ParameterResolver.CollectInitializeNodes(wi);
        if (inits.Count == 0) return;

        var sawLayeredConfig = false;
        var tagHasPipelineEntry = false;

        foreach (var init in inits)
        {
            // A tokenised path cannot be resolved without a run, so judging it would flag every
            // templated pipeline.
            if (ContainsToken(init.ParameterFile)) continue;

            var initPath = $"{path}.Initialize[{init.Tag}]";

            if (!File.Exists(init.ParameterFile))
            {
                issues.Add(new(WatchIssueSeverity.Warning, initPath,
                    $"Parameter file '{init.ParameterFile}' does not exist.",
                    "The run will stop before any action until this file is present."));
                continue;
            }

            if (!ParameterResolver.IsLayeredConfig(init.ParameterFile)) continue;

            var parsed = ParameterResolver.ReadJsonConfig(init.ParameterFile);
            if (parsed is null)
            {
                issues.Add(new(WatchIssueSeverity.Warning, initPath,
                    $"Parameter file '{init.ParameterFile}' is not valid JSON.",
                    "The run will stop before any action until it parses."));
                continue;
            }

            sawLayeredConfig = true;
            tagHasPipelineEntry |= parsed.Pipelines.ContainsKey(wi.Tag);

            if (!string.IsNullOrWhiteSpace(init.Profile) && !parsed.Profiles.ContainsKey(init.Profile))
            {
                var known = parsed.Profiles.Count == 0
                    ? "that file defines no profiles"
                    : "available: " + string.Join(", ", parsed.Profiles.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
                issues.Add(new(WatchIssueSeverity.Warning, initPath,
                    $"Profile '{init.Profile}' is not defined in '{Path.GetFileName(init.ParameterFile)}' ({known}).",
                    "Fix the Profile attribute or add the profile to the config."));
            }
        }

        if (sawLayeredConfig && !tagHasPipelineEntry)
        {
            issues.Add(new(WatchIssueSeverity.Warning, path,
                $"No 'pipelines' entry is keyed '{wi.Tag}', so this pipeline gets no per-pipeline overrides.",
                "The pipelines key must match the WatchItem Tag exactly; renaming the Tag silently drops the overrides."));
        }

        AnalyzeUndefinedTokens(config, wi, path, issues);
    }

    /// <summary>
    /// Flags [Token]s an action uses that no layer defines. Tokens the trigger file supplies at run
    /// time are excluded - they are legitimately absent from the config.
    /// </summary>
    private static void AnalyzeUndefinedTokens(
        WatchListConfig config, WatchItemConfig wi, string path, List<ValidationIssue> issues)
    {
        var ctx = new PipelineExecutionContext { WatchItemTag = wi.Tag };

        if (!string.IsNullOrWhiteSpace(config.GlobalVariablesFile) && File.Exists(config.GlobalVariablesFile))
        {
            if (ParameterResolver.IsLayeredConfig(config.GlobalVariablesFile))
            {
                // Hand it to the funnel instead of parsing here: it must be applied AFTER Initialize
                // (the release is not known before that) and at GlobalVars rank, or the validator and
                // the executor would disagree about which build a pipeline actually gets. The CSV
                // parser would also turn whole JSON lines into key names.
                ctx.GlobalVariablesFile = config.GlobalVariablesFile;
            }
            else
            {
                ParameterResolver.LoadParameterFile(ctx, config.GlobalVariablesFile, ParameterRank.Global);
            }
        }

        ParameterResolver.LoadForWatchItem(ctx, wi);

        // Supplied by the trigger file that fires the run, so their absence here is expected.
        foreach (var runtimeKey in new[] { wi.BuildNumberField, wi.DropLocationField })
        {
            if (string.IsNullOrWhiteSpace(runtimeKey)) continue;
            ParameterResolver.SetParameter(ctx, runtimeKey, "", ParameterRank.TriggerFile);
            ParameterResolver.SetParameter(ctx, "_" + runtimeKey.TrimStart('_'), "", ParameterRank.TriggerFile);
        }

        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ev in wi.Events)
            WalkForTokens(ev.Children, $"{path}.Event[{ev.Type}]", config, ctx, reported, issues);
    }

    private static void WalkForTokens(
        IEnumerable<IActionNode> nodes, string path, WatchListConfig config,
        PipelineExecutionContext ctx, HashSet<string> reported, List<ValidationIssue> issues)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case ActionGroupConfig ag:
                    WalkForTokens(ag.Children, $"{path}.Group[{ag.Tag}]", config, ctx, reported, issues);
                    break;

                case RefConfig r:
                    var template = config.Templates.FirstOrDefault(
                        t => string.Equals(t.ID, r.TemplateID, StringComparison.OrdinalIgnoreCase));
                    if (template is not null)
                        WalkForTokens(template.Children, $"{path}.Ref[{r.TemplateID}]", config, ctx, reported, issues);
                    break;

                case ActionConfig a:
                    foreach (var token in ParameterResolver.FindUnresolvedTokens(a, ctx))
                    {
                        if (!reported.Add(token)) continue;
                        issues.Add(new(WatchIssueSeverity.Warning,
                            $"{path}.Action[{(!string.IsNullOrEmpty(a.Tag) ? a.Tag : a.ResolvedTag)}]",
                            $"Token [{token}] is not defined by any parameter layer (global, profile or pipelines).",
                            "Add it to the parameter file, or the action will be refused at dispatch."));
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Flags RunRemoteCommand actions targeting an agent that is not registered.
    /// </summary>
    /// <remarks>
    /// A tokenised name (<c>[_Agent1]</c>) resolves at run time from the parameter file, so it cannot
    /// be checked here and must never be flagged - doing so would light up every templated pipeline.
    /// </remarks>
    private static void AnalyzeAgentNames(
        WatchListConfig config, IReadOnlyCollection<string> knownAgents, List<ValidationIssue> issues)
    {
        var roster = new HashSet<string>(knownAgents, StringComparer.OrdinalIgnoreCase);

        foreach (var wi in config.WatchItems)
            foreach (var ev in wi.Events)
                WalkForAgents(ev.Children, $"WatchItem[{wi.Tag}]/Event[{ev.Type}]", roster, issues);

        foreach (var t in config.Templates)
            WalkForAgents(t.Children, $"Template[{t.ID}]", roster, issues);
    }

    private static void WalkForAgents(
        IEnumerable<IActionNode> nodes, string path,
        HashSet<string> roster, List<ValidationIssue> issues)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case ActionGroupConfig ag:
                    WalkForAgents(ag.Children, $"{path}/ActionGroup[{ag.Tag}]", roster, issues);
                    break;

                case ActionConfig a when a.Type == ActionType.RunRemoteCommand:
                    var name = a.AgentName?.Trim() ?? "";
                    if (name.Length == 0 || ContainsToken(name) || roster.Contains(name))
                        break;
                    issues.Add(new(WatchIssueSeverity.Error, $"{path}/Action[{a.Tag}]",
                        $"Agent '{name}' is not a registered agent.",
                        $"Pick one of: {string.Join(", ", roster.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}"));
                    break;
            }
        }
    }

    private static bool ContainsToken(string value) => value.Contains('[') && value.Contains(']');

    private static void AnalyzeWatchItemWarnings(WatchItemConfig wi, List<ValidationIssue> issues)
    {
        var path = $"WatchItem[{wi.Tag}]";

        // A literal build number where a FIELD NAME is expected (real-world inconsistency).
        if (LooksLikeLiteralBuild(wi.BuildNumberField))
            issues.Add(new(WatchIssueSeverity.Warning, path,
                $"BuildNumberField contains what looks like a literal build number " +
                $"('{wi.BuildNumberField}') rather than a field name.",
                "Use a field name like 'BuildNumber'; the value comes from the trigger/parameter file."));

        // A UNC path where a field name is expected.
        if (LooksLikeLiteralDropPath(wi.DropLocationField))
            issues.Add(new(WatchIssueSeverity.Warning, path,
                $"DropLocationField contains what looks like a literal path " +
                $"('{wi.DropLocationField}') rather than a field name.",
                "Use a field name like 'DropLocation'."));

        foreach (var ev in wi.Events)
            AnalyzeChildrenWarnings(ev.Children, $"{path}.Event[{ev.Type}]", issues);
    }

    private static void AnalyzeChildrenWarnings(
        List<IActionNode> children, string context, List<ValidationIssue> issues)
    {
        foreach (var node in children)
        {
            switch (node)
            {
                case ActionConfig action:
                    AnalyzeActionWarnings(action, context, issues);
                    break;

                case ActionGroupConfig group:
                    var groupCtx = $"{context}.Group[{group.Tag}]";
                    if (group.Children.Count == 0)
                        issues.Add(new(WatchIssueSeverity.Warning, groupCtx,
                            "ActionGroup is empty (no actions). It will do nothing.",
                            "Add an action or remove the group."));
                    // A group that still has children but skips every one of them is almost always a
                    // half-finished edit: skipping the group itself says the same thing far more clearly.
                    else if (group.Children.All(IsSkipped))
                        issues.Add(new(WatchIssueSeverity.Warning, groupCtx,
                            $"Every child of this ActionGroup is skipped ({group.Children.Count}), so the group does nothing.",
                            "Skip the group itself, or include at least one child again."));
                    AnalyzeChildrenWarnings(group.Children, groupCtx, issues);
                    break;

                case InitializeConfig init when string.IsNullOrWhiteSpace(init.ParameterFile):
                    issues.Add(new(WatchIssueSeverity.Warning, context,
                        "Initialize has no ParameterFile; no tokens will be loaded."));
                    break;
            }
        }
    }

    /// <summary>A Ref cannot be skipped (it is not an <see cref="ISkippableNode"/>), so it never counts.</summary>
    private static bool IsSkipped(IActionNode node)
        => node is ISkippableNode { Skip: true };

    private static void AnalyzeActionWarnings(
        ActionConfig action, string context, List<ValidationIssue> issues)
    {
        var label = !string.IsNullOrEmpty(action.Tag) ? action.Tag : action.ResolvedTag;
        var path = $"{context}.Action[{label}]";

        // Absurd timeout (below the hard-error ceiling but still suspicious).
        if (action.Timeout > SuspiciousTimeoutSeconds && action.Timeout <= MaxTimeoutSeconds)
            issues.Add(new(WatchIssueSeverity.Warning, path,
                $"Timeout is {action.Timeout}s (over 24 hours). Likely a typo.",
                "Typical timeouts are seconds: 120, 360, 2700."));

        // PollInterval vs Timeout. NOTE: in this codebase PollInterval is in
        // MILLISECONDS (default 1000) while Timeout is in SECONDS — so compare
        // in a common unit. A poll that is longer than the whole timeout window
        // would never fire.
        if (action.PollInterval > 0 && action.Timeout > 0 &&
            action.PollInterval > action.Timeout * 1000L)
            issues.Add(new(WatchIssueSeverity.Warning, path,
                $"PollInterval ({action.PollInterval}ms) is larger than the whole " +
                $"Timeout window ({action.Timeout}s); the poll would never fire.",
                "Set PollInterval (ms) smaller than Timeout×1000 (e.g. 30000)."));

        // Plaintext password stored in the XML.
        if (!string.IsNullOrEmpty(action.Password))
            issues.Add(new(WatchIssueSeverity.Warning, path,
                "Password is stored in plaintext in the XML.",
                "Move credentials to a secured parameter file or token store."));
    }

    private static bool LooksLikeLiteralBuild(string s) =>
        // e.g. OAK_main_20260601.7 — letters_word_yyyymmdd.revision
        System.Text.RegularExpressions.Regex.IsMatch(
            s ?? "", @"^[A-Za-z]+_\w+_\d{8}\.\d+$");

    private static bool LooksLikeLiteralDropPath(string s) =>
        (s ?? "").StartsWith(@"\\");   // a UNC path where a field name is expected
}

/// <summary>Severity bucket for a builder validation issue.</summary>
public enum WatchIssueSeverity { Error, Warning, Info }

/// <summary>
/// A single severity-tagged validation issue surfaced by
/// <see cref="WatchListValidator.Analyze"/>. Errors block save; warnings are advisory.
/// </summary>
public sealed record ValidationIssue(
    WatchIssueSeverity Severity,
    string NodePath,
    string Message,
    string? FixHint = null)
{
    public override string ToString() =>
        $"[{Severity}] {NodePath}: {Message}" + (FixHint is null ? "" : $" ({FixHint})");
}

/// <summary>Represents a single validation error found in a WatchList configuration.</summary>
public sealed record WatchListValidationError(string Context, string Tag, string Message)
{
    public override string ToString() => $"[{Context}] {Message}";
}
