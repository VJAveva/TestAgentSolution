using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.ViewModels;

public sealed partial class TreeNodeViewModel : ObservableObject
{
    [ObservableProperty] private string _displayText = "";
    [ObservableProperty] private string _nodeIcon = "?";
    [ObservableProperty] private string _nodeKind = "";
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private bool _isSelected;

    // ── Semantic icon glyph (Segoe MDL2 Assets) ────────────────────
    [ObservableProperty] private string _nodeIconGlyph = "\uE8A5"; // Document

    // ── Token-resolved display text (UI-only, does not modify model) ──
    [ObservableProperty] private string _resolvedDisplayText = "";
    [ObservableProperty] private bool _hasUnresolvedTokens;
    [ObservableProperty] private string _unresolvedTokenTooltip = "";

    [ObservableProperty] private string _tag = "";
    [ObservableProperty] private string _executionTypeText = "Sequential";
    [ObservableProperty] private bool _failAndContinue;
    [ObservableProperty] private string _watchPath = "";
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private string _eventType = "Renamed";
    [ObservableProperty] private string _buildNumberField = "BuildNumber";
    [ObservableProperty] private string _dropLocationField = "DropLocation";
    [ObservableProperty] private string _lastBuildNumber = "";
    [ObservableProperty] private string _lastDropLocation = "";
    [ObservableProperty] private string _buildBasePath = "";
    [ObservableProperty] private string _selectedBuildPath = "";
    [ObservableProperty] private string _actionTypeText = "RunCommand";
    [ObservableProperty] private string _agentName = "";
    [ObservableProperty] private string _command = "";
    [ObservableProperty] private string _parameters = "";
    [ObservableProperty] private int _timeout;
    [ObservableProperty] private int _pollInterval = 1000;
    [ObservableProperty] private bool _isReboot;
    [ObservableProperty] private string _completionCheckCommand = "";
    [ObservableProperty] private int _completionPollIntervalSeconds = 30;
    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _from = "";
    [ObservableProperty] private string _to = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _body = "";
    [ObservableProperty] private string _attachment = "";
    [ObservableProperty] private string _embed = "";
    [ObservableProperty] private string _largeFilesShare = "";
    [ObservableProperty] private int _maxRetries;
    [ObservableProperty] private int _retryDelaySeconds = 10;
    [ObservableProperty] private string _retryBackoff = "Exponential";
    [ObservableProperty] private string _retryOnExitCodes = "";
    [ObservableProperty] private string _parameterFile = "";
    [ObservableProperty] private string _templateID = "";
    [ObservableProperty] private string _templateName = "";
    [ObservableProperty] private int _childCount;

    // ── Phase 3b: Pipeline lock badge (per-row, bound in TreeViewSpec.xaml) ──
    [ObservableProperty] private LockBadgeViewModel? _lockBadge;

    /// <summary>Returns the Parameters string with [Token] placeholders resolved. Read-only display value.</summary>
    public string ResolvedParameters => ResolveTokens(Parameters);

    /// <summary>Returns the AgentName string with [Token] placeholders resolved. Read-only display value.</summary>
    public string ResolvedAgentName => ResolveTokens(AgentName);

    /// <summary>Called by source generator when Parameters changes — refreshes ResolvedParameters.</summary>
    partial void OnParametersChanged(string value)
    {
        OnPropertyChanged(nameof(ResolvedParameters));
    }

    /// <summary>Called by source generator when AgentName changes — refreshes ResolvedAgentName.</summary>
    partial void OnAgentNameChanged(string value)
    {
        OnPropertyChanged(nameof(ResolvedAgentName));
    }

    /// <summary>Auto-sync IsEnabled toggle back to the model (e.g. WatchItemConfig.IsEnabled).</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    private static bool _suppressIsEnabledPropagation;

    partial void OnIsEnabledChanged(bool value)
    {
        if (ModelObject is WatchItemConfig wi)
            wi.IsEnabled = value;

        // Propagate parent → children: when WatchList root is toggled, update all WatchItem children
        if (!_suppressIsEnabledPropagation && NodeKind == NodeKinds.WatchList)
        {
            _suppressIsEnabledPropagation = true;
            try
            {
                foreach (var child in Children)
                {
                    if (child.NodeKind == NodeKinds.WatchItem)
                        child.IsEnabled = value;
                }
            }
            finally
            {
                _suppressIsEnabledPropagation = false;
            }
        }
    }

    /// <summary>
    /// Computes whether all WatchItem children are enabled (true), none (false), or mixed (null).
    /// Used by UI for three-state display on the WatchList root.
    /// </summary>
    public bool? ComputeChildrenEnabledState()
    {
        if (NodeKind != NodeKinds.WatchList || Children.Count == 0) return IsEnabled;
        var watchItems = Children.Where(c => c.NodeKind == NodeKinds.WatchItem).ToList();
        if (watchItems.Count == 0) return IsEnabled;
        bool allEnabled = watchItems.All(c => c.IsEnabled);
        bool allDisabled = watchItems.All(c => !c.IsEnabled);
        if (allEnabled) return true;
        if (allDisabled) return false;
        return null; // mixed
    }

    // ── Tree search/filter visibility ───────────────────────────────
    [ObservableProperty] private bool _isFilterVisible = true;

    // ── Execution status ────────────────────────────────────────────
    // Values: "Idle", "Running", "Success", "Failed", "PartialFailure", "Cancelled"
    [ObservableProperty] private string _executionStatus = "Idle";
    [ObservableProperty] private string _statusSymbol = "";
    [ObservableProperty] private string _statusColor = "Transparent";
    [ObservableProperty] private string _statusTooltip = "";
    [ObservableProperty] private string _failureMessage = "";

    private string _lastExecutionError = "";
    public string LastExecutionError
    {
        get => _lastExecutionError;
        set => SetProperty(ref _lastExecutionError, value);
    }

    private int _lastExitCode;
    public int LastExitCode
    {
        get => _lastExitCode;
        set => SetProperty(ref _lastExitCode, value);
    }

    partial void OnExecutionStatusChanged(string value)
    {
        switch (value)
        {
            case "Running":
                StatusSymbol = "\u25B6";  // ▶
                StatusColor = "#FFF9E2AF"; // Amber
                StatusTooltip = "Running...";
                break;
            case "Success":
                StatusSymbol = "\u2714";  // ✔
                StatusColor = "#FFA6E3A1"; // Green
                StatusTooltip = "Completed successfully";
                FailureMessage = "";
                break;
            case var s when s is not null && s.StartsWith("Failed"):
                StatusSymbol = "\u2716";  // ✖
                StatusColor = "#FFF38BA8"; // Red
                StatusTooltip = string.IsNullOrEmpty(_lastExecutionError)
                    ? "Execution failed"
                    : $"Failed: {_lastExecutionError}";
                break;
            case "PartialFailure":
                StatusSymbol = "\u26A0";  // ⚠
                StatusColor = "#FFF9E2AF"; // Amber
                StatusTooltip = "Some actions failed";
                break;
            case "Cancelled":
                StatusSymbol = "\u2298";  // ⊘
                StatusColor = "#FF9399B2"; // Gray
                StatusTooltip = "Cancelled";
                break;
            default: // Idle
                StatusSymbol = "";
                StatusColor = "Transparent";
                StatusTooltip = "";
                FailureMessage = "";
                break;
        }
    }

    /// <summary>Set failure status with a specific error message.</summary>
    public void SetFailed(string message)
    {
        FailureMessage = message;
        ExecutionStatus = "Failed";
    }

    /// <summary>Mark this node as Cancelled and recursively cancel any descendants still showing Running.</summary>
    public void CancelWithDescendants()
    {
        ExecutionStatus = "Cancelled";
        CancelRunningDescendants();
    }

    private void CancelRunningDescendants()
    {
        foreach (var c in Children)
        {
            if (c.ExecutionStatus == "Running")
                c.ExecutionStatus = "Cancelled";
            c.CancelRunningDescendants();
        }
    }

    /// <summary>Recursively set status on this node and all descendants.</summary>
    public void SetStatusRecursive(string status)
    {
        ExecutionStatus = status;
        foreach (var c in Children) c.SetStatusRecursive(status);
    }

    /// <summary>Reset execution status to Idle on this node and all descendants.</summary>
    public void ResetStatus() => SetStatusRecursive("Idle");

    /// <summary>
    /// Propagate aggregated status upward from this node to root.
    /// Priority: Failed > Running > Success > Idle.
    /// Also auto-expands parent nodes when a child fails.
    /// </summary>
    public void PropagateStatusUp()
    {
        var p = Parent;
        while (p is not null)
        {
            var aggregated = ComputeAggregatedStatus(p);
            if (p.ExecutionStatus != aggregated)
                p.ExecutionStatus = aggregated;

            // Auto-expand parents on failure so the failed node is visible
            if (ExecutionStatus is "Failed" or "PartialFailure")
                p.IsExpanded = true;

            p = p.Parent;
        }
    }

    /// <summary>
    /// Compute the aggregated status for a parent based on its children.
    /// Priority: Failed > Running > PartialFailure > Success > Idle.
    /// </summary>
    private static string ComputeAggregatedStatus(TreeNodeViewModel parent)
    {
        var hasFailed = false;
        var hasRunning = false;
        var hasSuccess = false;
        var hasCancelled = false;

        foreach (var child in parent.Children)
        {
            switch (child.ExecutionStatus)
            {
                case "Failed" or "PartialFailure": hasFailed = true; break;
                case "Running": hasRunning = true; break;
                case "Success": hasSuccess = true; break;
                case "Cancelled": hasCancelled = true; break;
            }
        }

        if (hasRunning) return "Running";
        if (hasFailed && hasSuccess) return "PartialFailure";
        if (hasFailed) return "Failed";
        if (hasCancelled && hasSuccess) return "PartialFailure";
        if (hasCancelled) return "Cancelled";
        if (hasSuccess) return "Success";
        return "Idle";
    }

    public ObservableCollection<TreeNodeViewModel> Children { get; } = new();
    public object? ModelObject { get; set; }
    public TreeNodeViewModel? Parent { get; set; }

    // ── Token resolution for display ────────────────────────────────

    private static readonly Regex TokenPattern = new(@"\[(\w+)\]", RegexOptions.Compiled);

    /// <summary>
    /// Shared token dictionary. Populated from loaded parameter files and runtime context.
    /// Keys are token names (without brackets), values are resolved values.
    /// </summary>
    public static Dictionary<string, string> TokenValues { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Called by source generator when DisplayText changes.</summary>
    partial void OnDisplayTextChanged(string value)
    {
        UpdateResolvedText(value);
    }

    /// <summary>
    /// Resolves [Token] placeholders in the input string using TokenValues dictionary.
    /// Returns the resolved string. Unresolved tokens are left as-is.
    /// </summary>
    public static string ResolveTokens(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        if (!input.Contains('[')) return input;

        return TokenPattern.Replace(input, match =>
        {
            var key = match.Groups[1].Value;
            if (TokenValues.TryGetValue(key, out var val))
                return val;
            // Also try with leading underscore removed
            if (key.StartsWith('_') && TokenValues.TryGetValue(key[1..], out val))
                return val;
            return match.Value; // leave unresolved
        });
    }

    /// <summary>Updates ResolvedDisplayText and unresolved-token metadata.</summary>
    private void UpdateResolvedText(string rawText)
    {
        var resolved = ResolveTokens(rawText);
        ResolvedDisplayText = resolved;

        // Check for remaining unresolved tokens
        var remaining = TokenPattern.Matches(resolved);
        if (remaining.Count > 0)
        {
            HasUnresolvedTokens = true;
            var names = string.Join(", ", remaining.Select(m => m.Value).Distinct());
            UnresolvedTokenTooltip = $"Unresolved tokens: {names}";
        }
        else
        {
            HasUnresolvedTokens = false;
            UnresolvedTokenTooltip = "";
        }
    }

    /// <summary>Refreshes resolved display text on this node and all descendants (e.g. after token dict changes).</summary>
    public void RefreshResolvedTextRecursive()
    {
        UpdateResolvedText(DisplayText);
        OnPropertyChanged(nameof(ResolvedParameters));
        OnPropertyChanged(nameof(ResolvedAgentName));
        foreach (var c in Children) c.RefreshResolvedTextRecursive();
    }

    // ── Semantic icon resolution ────────────────────────────────────

    /// <summary>
    /// Resolves a Segoe MDL2 Assets glyph based on node kind and action type.
    /// Node Type → Icon:
    ///   WatchList/TemplateList → &#xE8B7; (Folder)
    ///   WatchItem             → &#xE7B3; (View/Eye)
    ///   Event                 → &#xEA80; (LightningBolt)
    ///   Template              → &#xE8A5; (Document)
    ///   ActionGroup           → &#xE8CB; (BranchFork)
    ///   Initialize            → &#xE713; (Settings)
    ///   Action:RunRemoteCommand → &#xE839; (Remote)
    ///   Action:SendMail       → &#xE715; (Mail)
    ///   Action:RunCommand     → &#xE768; (Play)
    ///   Ref                   → &#xE71B; (Link)
    /// </summary>
    public static string ResolveNodeIconGlyph(string nodeKind, string actionType = "")
    {
        return nodeKind switch
        {
            NodeKinds.WatchList or NodeKinds.TemplateList => "\uE8B7", // Folder/List
            NodeKinds.WatchItem => "\uE7B3",                   // View/Eye
            NodeKinds.Event => "\uEA80",                       // LightningBolt
            NodeKinds.Template => "\uE8A5",                    // Document
            NodeKinds.ActionGroup => "\uE8CB",                 // BranchFork
            NodeKinds.Initialize => "\uE713",                  // Settings
            NodeKinds.Action => actionType switch
            {
                "RunRemoteCommand" => "\uE839",        // Remote/PC
                "SendMail" => "\uE715",                // Mail
                _ => "\uE768",                         // Play
            },
            NodeKinds.Ref => "\uE71B",                         // Link
            _ => "\uE8A5",                             // Document (fallback)
        };
    }

    // ── Root node factories ─────────────────────────────────────────

    public static TreeNodeViewModel FromWatchList(WatchListConfig config)
    {
        var name = !string.IsNullOrWhiteSpace(config.FilePath)
            ? Path.GetFileName(config.FilePath) : "New WatchList";
        var root = new TreeNodeViewModel
        {
            NodeKind = NodeKinds.WatchList, NodeIcon = "WL",
            NodeIconGlyph = ResolveNodeIconGlyph(NodeKinds.WatchList),
            DisplayText = $"WatchList  ({name}  \u2014  {config.WatchItems.Count} items)",
            ModelObject = config, IsExpanded = true,
            ChildCount = config.WatchItems.Count,
        };
        foreach (var wi in config.WatchItems)
        {
            var c = FromWatchItem(wi); c.Parent = root; root.Children.Add(c);
        }
        return root;
    }

    public static TreeNodeViewModel FromTemplateList(List<TemplateConfig> templates)
    {
        var root = new TreeNodeViewModel
        {
            NodeKind = NodeKinds.TemplateList, NodeIcon = "TL",
            NodeIconGlyph = ResolveNodeIconGlyph(NodeKinds.TemplateList),
            DisplayText = $"Templates  ({templates.Count} templates)",
            ModelObject = templates, IsExpanded = true,
            ChildCount = templates.Count,
        };
        foreach (var t in templates)
        {
            var c = FromTemplate(t); c.Parent = root; root.Children.Add(c);
        }
        return root;
    }

    // ── Leaf node factories ─────────────────────────────────────────

    public static string ResolveCommandIcon(string command, ActionType type)
    {
        if (type == ActionType.SendMail) return "M";
        var ext = "";
        try { if (command.Length > 0) ext = Path.GetExtension(command.Trim().Trim('"')).ToLowerInvariant(); }
        catch (ArgumentException) { /* command contains invalid path characters — use default icon */ }

        return ext switch
        {
            ".exe" => "X", ".bat" or ".cmd" => "B", ".ps1" => "P", ".msi" => "I",
            _ => type == ActionType.RunRemoteCommand ? "R" : "A"
        };
    }

    public static TreeNodeViewModel FromWatchItem(WatchItemConfig wi)
    {
        var label = !string.IsNullOrWhiteSpace(wi.Tag)
            ? $"{wi.Tag}  ({wi.Path}{wi.Filter})" : $"{wi.Path}{wi.Filter}";
        var node = new TreeNodeViewModel
        {
            NodeKind = NodeKinds.WatchItem, NodeIcon = "W",
            NodeIconGlyph = ResolveNodeIconGlyph(NodeKinds.WatchItem),
            Tag = wi.Tag,
            WatchPath = wi.Path, Filter = wi.Filter, IsEnabled = wi.IsEnabled,
            BuildNumberField = wi.BuildNumberField,
            DropLocationField = wi.DropLocationField,
            BuildBasePath = wi.BuildBasePath,
            LastBuildNumber = wi.LastBuildNumber ?? "",
            LastDropLocation = wi.LastDropLocation ?? "",
            DisplayText = label, ModelObject = wi,
        };
        foreach (var ev in wi.Events) { var c = FromEvent(ev); c.Parent = node; node.Children.Add(c); }
        return node;
    }

    public static TreeNodeViewModel FromEvent(EventConfig ev)
    {
        var node = new TreeNodeViewModel
        {
            NodeKind = NodeKinds.Event, NodeIcon = "E",
            NodeIconGlyph = ResolveNodeIconGlyph(NodeKinds.Event),
            EventType = ev.Type,
            ExecutionTypeText = ev.ExecutionType.ToString(),
            DisplayText = $"Event: {ev.Type} ({ev.ExecutionType})", ModelObject = ev,
        };
        foreach (var child in ev.Children) { var c = FromActionNode(child); c.Parent = node; node.Children.Add(c); }
        return node;
    }

    public static TreeNodeViewModel FromTemplate(TemplateConfig t)
    {
        var node = new TreeNodeViewModel
        {
            NodeKind = NodeKinds.Template, NodeIcon = "T",
            NodeIconGlyph = ResolveNodeIconGlyph(NodeKinds.Template),
            TemplateName = t.ID, Tag = t.ID,
            DisplayText = $"Template: {t.ID}", ModelObject = t,
        };
        foreach (var child in t.Children) { var c = FromActionNode(child); c.Parent = node; node.Children.Add(c); }
        return node;
    }

    public static TreeNodeViewModel FromActionNode(IActionNode n) => n switch
    {
        ActionGroupConfig ag => FromActionGroup(ag),
        ActionConfig a => FromAction(a),
        InitializeConfig init => FromInitialize(init),
        RefConfig r => FromRef(r),
        _ => new TreeNodeViewModel { DisplayText = "Unknown" },
    };

    public static TreeNodeViewModel FromActionGroup(ActionGroupConfig ag)
    {
        var node = new TreeNodeViewModel
        {
            NodeKind = NodeKinds.ActionGroup,
            NodeIcon = ag.ExecutionType == ExecutionMode.Parallel ? "||" : ">>",
            NodeIconGlyph = ResolveNodeIconGlyph(NodeKinds.ActionGroup),
            Tag = ag.Tag, ExecutionTypeText = ag.ExecutionType.ToString(),
            FailAndContinue = ag.FailAndContinue,
            DisplayText = $"[{ag.ExecutionType}] {ag.Tag}", ModelObject = ag,
        };
        foreach (var child in ag.Children) { var c = FromActionNode(child); c.Parent = node; node.Children.Add(c); }
        return node;
    }

    public static TreeNodeViewModel FromAction(ActionConfig a)
    {
        var icon = ResolveCommandIcon(a.Command, a.Type);
        var tag = a.ResolvedTag;
        var label = a.Type switch
        {
            ActionType.RunRemoteCommand => !string.IsNullOrWhiteSpace(a.AgentName)
                ? $"Remote Command on '{a.AgentName}' \u2014 {a.Command}"
                : $"Remote Command \u2014 {a.Command}",
            ActionType.RunCommand => $"Run \u2014 {a.Command} {a.Parameters}".TrimEnd(),
            ActionType.SendMail => $"Send Mail to {a.To}: {a.Title}",
            _ => a.Command,
        };
        if (label.Length > 100) label = label[..100] + "\u2026";
        return new TreeNodeViewModel
        {
            NodeKind = NodeKinds.Action, NodeIcon = icon,
            NodeIconGlyph = ResolveNodeIconGlyph(NodeKinds.Action, a.Type.ToString()),
            ActionTypeText = a.Type.ToString(),
            Tag = tag,
            AgentName = a.AgentName, Command = a.Command, Parameters = a.Parameters,
            Timeout = a.Timeout, PollInterval = a.PollInterval,
            FailAndContinue = a.FailAndContinue, IsReboot = a.IsReboot,
            CompletionCheckCommand = a.CompletionCheckCommand,
            CompletionPollIntervalSeconds = a.CompletionPollIntervalSeconds,
            UserName = a.UserName, Password = a.Password,
            From = a.From, To = a.To, Title = a.Title, Body = a.Body,
            Attachment = a.Attachment, Embed = a.Embed, LargeFilesShare = a.LargeFilesShare,
            MaxRetries = a.MaxRetries, RetryDelaySeconds = a.RetryDelaySeconds,
            RetryBackoff = a.RetryBackoff, RetryOnExitCodes = a.RetryOnExitCodes,
            DisplayText = label, ModelObject = a,
        };
    }

    /// <summary>Derives a readable Tag for an Action node from its type and command/mail fields.</summary>
    private static string DeriveActionTag(ActionConfig a)
    {
        return a.Type switch
        {
            ActionType.RunRemoteCommand => !string.IsNullOrWhiteSpace(a.AgentName)
                ? $"{a.AgentName}:{GetCommandFileName(a.Command)}"
                : GetCommandFileName(a.Command),
            ActionType.SendMail => !string.IsNullOrWhiteSpace(a.Title)
                ? $"Mail:{a.Title}" : "SendMail",
            _ => GetCommandFileName(a.Command),
        };
    }

    /// <summary>Extracts a short filename from a command path for use in tags.</summary>
    private static string GetCommandFileName(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "Action";
        try
        {
            var trimmed = command.Trim().Trim('"');
            return Path.GetFileName(trimmed);
        }
        catch
        {
            return command.Length > 30 ? command[..30] : command;
        }
    }

    public void EnsureDefaultActionTag()
    {
        if (NodeKind != NodeKinds.Action || !string.IsNullOrWhiteSpace(Tag)) return;

        var actionType = Enum.TryParse<ActionType>(ActionTypeText, out var at)
            ? at
            : ActionType.RunCommand;

        var action = new ActionConfig
        {
            Type = actionType,
            AgentName = AgentName,
            Command = Command,
            To = To,
            Title = Title
        };

        Tag = action.ResolvedTag;
    }

    public static TreeNodeViewModel FromInitialize(InitializeConfig init) => new()
    {
        NodeKind = NodeKinds.Initialize, NodeIcon = "i",
        NodeIconGlyph = ResolveNodeIconGlyph(NodeKinds.Initialize),
        Tag = init.Tag,
        ParameterFile = init.ParameterFile,
        DisplayText = $"Initialize: {init.ParameterFile}", ModelObject = init,
    };

    public static TreeNodeViewModel FromRef(RefConfig r) => new()
    {
        NodeKind = NodeKinds.Ref, NodeIcon = ">",
        NodeIconGlyph = ResolveNodeIconGlyph(NodeKinds.Ref),
        TemplateID = r.TemplateID,
        DisplayText = $"Ref > {r.TemplateID}", ModelObject = r,
    };

    // ── Write-back & refresh ────────────────────────────────────────

    public void ApplyToModel()
    {
        switch (ModelObject)
        {
            case WatchItemConfig wi:
                wi.Tag = Tag; wi.Path = WatchPath; wi.Filter = Filter; wi.IsEnabled = IsEnabled;
                wi.BuildNumberField = BuildNumberField;
                wi.DropLocationField = DropLocationField;
                wi.BuildBasePath = BuildBasePath;
                break;
            case EventConfig ev:
                ev.Type = EventType;
                ev.ExecutionType = Enum.TryParse<ExecutionMode>(ExecutionTypeText, out var em) ? em : ExecutionMode.Sequential;
                break;
            case ActionGroupConfig ag:
                ag.Tag = Tag;
                ag.ExecutionType = Enum.TryParse<ExecutionMode>(ExecutionTypeText, out var am) ? am : ExecutionMode.Sequential;
                ag.FailAndContinue = FailAndContinue; break;
            case ActionConfig a:
                EnsureDefaultActionTag();
                a.Type = Enum.TryParse<ActionType>(ActionTypeText, out var at) ? at : ActionType.RunCommand;
                a.Tag = Tag;
                a.AgentName = AgentName; a.Command = Command; a.Parameters = Parameters;
                a.Timeout = Timeout; a.PollInterval = PollInterval;
                a.FailAndContinue = FailAndContinue; a.IsReboot = IsReboot;
                a.CompletionCheckCommand = CompletionCheckCommand;
                a.CompletionPollIntervalSeconds = CompletionPollIntervalSeconds;
                a.UserName = UserName; a.Password = Password;
                a.From = From; a.To = To; a.Title = Title; a.Body = Body;
                a.Attachment = Attachment; a.Embed = Embed; a.LargeFilesShare = LargeFilesShare;
                a.MaxRetries = MaxRetries; a.RetryDelaySeconds = RetryDelaySeconds;
                a.RetryBackoff = RetryBackoff; a.RetryOnExitCodes = RetryOnExitCodes; break;
            case InitializeConfig init:
                init.Tag = Tag; init.ParameterFile = ParameterFile; break;
            case RefConfig r:
                r.TemplateID = TemplateID; break;
            case TemplateConfig t:
                t.ID = TemplateName; break;
        }
    }

    public void RefreshDisplayText()
    {
        ChildCount = Children.Count;
        DisplayText = NodeKind switch
        {
            NodeKinds.WatchList => $"WatchList  ({Children.Count} items)",
            NodeKinds.TemplateList => $"Templates  ({Children.Count} templates)",
            NodeKinds.WatchItem => !string.IsNullOrWhiteSpace(Tag) ? $"{Tag}  ({WatchPath}{Filter})" : $"{WatchPath}{Filter}",
            NodeKinds.Event => $"Event: {EventType} ({ExecutionTypeText})",
            NodeKinds.ActionGroup => $"[{ExecutionTypeText}] {Tag}",
            NodeKinds.Action => FormatActionLabel(ActionTypeText, AgentName, Command, Parameters, To, Title),
            NodeKinds.Initialize => $"Initialize: {ParameterFile}",
            NodeKinds.Ref => $"Ref > {TemplateID}",
            NodeKinds.Template => $"Template: {TemplateName}",
            _ => DisplayText,
        };
    }

    private static string FormatActionLabel(string actionType, string agent, string cmd, string param, string to, string title)
    {
        var label = actionType switch
        {
            "RunRemoteCommand" => !string.IsNullOrWhiteSpace(agent)
                ? $"Remote Command on '{agent}' \u2014 {cmd}"
                : $"Remote Command \u2014 {cmd}",
            "RunCommand" => $"Run \u2014 {cmd} {param}".TrimEnd(),
            "SendMail" => $"Send Mail to {to}: {title}",
            _ => cmd,
        };
        return label.Length > 100 ? label[..100] + "\u2026" : label;
    }

    /// <summary>Find the TreeNodeViewModel whose ModelObject matches the given IActionNode (by NodeId or reference).</summary>
    public TreeNodeViewModel? FindByModel(object model)
    {
        if (ReferenceEquals(ModelObject, model)) return this;
        // Match by stable NodeId so snapshot-cloned nodes resolve correctly
        if (model is IActionNode target && ModelObject is IActionNode mine
            && target.NodeId == mine.NodeId)
            return this;
        foreach (var c in Children)
        {
            var found = c.FindByModel(model);
            if (found is not null) return found;
        }
        return null;
    }
}
