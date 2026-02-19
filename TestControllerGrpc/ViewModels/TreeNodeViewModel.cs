using System.Collections.ObjectModel;
using System.IO;
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

    [ObservableProperty] private string _tag = "";
    [ObservableProperty] private string _executionTypeText = "Sequential";
    [ObservableProperty] private bool _failAndContinue;
    [ObservableProperty] private string _watchPath = "";
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private string _eventType = "Renamed";
    [ObservableProperty] private string _actionTypeText = "RunCommand";
    [ObservableProperty] private string _agentName = "";
    [ObservableProperty] private string _command = "";
    [ObservableProperty] private string _parameters = "";
    [ObservableProperty] private int _timeout;
    [ObservableProperty] private int _pollInterval = 1000;
    [ObservableProperty] private bool _isReboot;
    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _from = "";
    [ObservableProperty] private string _to = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _body = "";
    [ObservableProperty] private string _attachment = "";
    [ObservableProperty] private string _embed = "";
    [ObservableProperty] private string _largeFilesShare = "";
    [ObservableProperty] private string _parameterFile = "";
    [ObservableProperty] private string _templateID = "";
    [ObservableProperty] private string _templateName = "";
    [ObservableProperty] private int _childCount;

    // ── Execution status ────────────────────────────────────────────
    // Values: "Idle", "Running", "Success", "Failed"
    [ObservableProperty] private string _executionStatus = "Idle";
    [ObservableProperty] private string _statusSymbol = "";
    [ObservableProperty] private string _statusColor = "Transparent";

    partial void OnExecutionStatusChanged(string value)
    {
        switch (value)
        {
            case "Running":
                StatusSymbol = "\u25B6";  // ▶
                StatusColor = "#FF89B4FA"; // Accent blue
                break;
            case "Success":
                StatusSymbol = "\u2714";  // ✔
                StatusColor = "#FFA6E3A1"; // Green
                break;
            case "Failed":
                StatusSymbol = "\u2716";  // ✖
                StatusColor = "#FFF38BA8"; // Red
                break;
            default: // Idle
                StatusSymbol = "";
                StatusColor = "Transparent";
                break;
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

    public ObservableCollection<TreeNodeViewModel> Children { get; } = new();
    public object? ModelObject { get; set; }
    public TreeNodeViewModel? Parent { get; set; }

    // ── Root node factories ─────────────────────────────────────────

    public static TreeNodeViewModel FromWatchList(WatchListConfig config)
    {
        var name = !string.IsNullOrWhiteSpace(config.FilePath)
            ? Path.GetFileName(config.FilePath) : "New WatchList";
        var root = new TreeNodeViewModel
        {
            NodeKind = "WatchList", NodeIcon = "WL",
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
            NodeKind = "TemplateList", NodeIcon = "TL",
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
        try { if (command.Length > 0) ext = Path.GetExtension(command.Trim().Trim('"')).ToLowerInvariant(); } catch { }
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
            NodeKind = "WatchItem", NodeIcon = "W", Tag = wi.Tag,
            WatchPath = wi.Path, Filter = wi.Filter, IsEnabled = wi.IsEnabled,
            DisplayText = label, ModelObject = wi,
        };
        foreach (var ev in wi.Events) { var c = FromEvent(ev); c.Parent = node; node.Children.Add(c); }
        return node;
    }

    public static TreeNodeViewModel FromEvent(EventConfig ev)
    {
        var node = new TreeNodeViewModel
        {
            NodeKind = "Event", NodeIcon = "E", EventType = ev.Type,
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
            NodeKind = "Template", NodeIcon = "T", TemplateName = t.ID, Tag = t.ID,
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
            NodeKind = "ActionGroup",
            NodeIcon = ag.ExecutionType == ExecutionMode.Parallel ? "||" : ">>",
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
        var label = a.Type switch
        {
            ActionType.RunRemoteCommand => $"{a.AgentName}: {a.Command}",
            ActionType.RunCommand => $"{a.Command} {a.Parameters}",
            ActionType.SendMail => $"Mail > {a.To}: {a.Title}",
            _ => a.Command,
        };
        if (label.Length > 80) label = label[..80] + "...";
        return new TreeNodeViewModel
        {
            NodeKind = "Action", NodeIcon = icon, ActionTypeText = a.Type.ToString(),
            AgentName = a.AgentName, Command = a.Command, Parameters = a.Parameters,
            Timeout = a.Timeout, PollInterval = a.PollInterval,
            FailAndContinue = a.FailAndContinue, IsReboot = a.IsReboot,
            UserName = a.UserName, Password = a.Password,
            From = a.From, To = a.To, Title = a.Title, Body = a.Body,
            Attachment = a.Attachment, Embed = a.Embed, LargeFilesShare = a.LargeFilesShare,
            DisplayText = label, ModelObject = a,
        };
    }

    public static TreeNodeViewModel FromInitialize(InitializeConfig init) => new()
    {
        NodeKind = "Initialize", NodeIcon = "i", Tag = init.Tag,
        ParameterFile = init.ParameterFile,
        DisplayText = $"Initialize: {init.ParameterFile}", ModelObject = init,
    };

    public static TreeNodeViewModel FromRef(RefConfig r) => new()
    {
        NodeKind = "Ref", NodeIcon = ">", TemplateID = r.TemplateID,
        DisplayText = $"Ref > {r.TemplateID}", ModelObject = r,
    };

    // ── Write-back & refresh ────────────────────────────────────────

    public void ApplyToModel()
    {
        switch (ModelObject)
        {
            case WatchItemConfig wi:
                wi.Tag = Tag; wi.Path = WatchPath; wi.Filter = Filter; wi.IsEnabled = IsEnabled; break;
            case EventConfig ev:
                ev.Type = EventType;
                ev.ExecutionType = Enum.TryParse<ExecutionMode>(ExecutionTypeText, out var em) ? em : ExecutionMode.Sequential; break;
            case ActionGroupConfig ag:
                ag.Tag = Tag;
                ag.ExecutionType = Enum.TryParse<ExecutionMode>(ExecutionTypeText, out var am) ? am : ExecutionMode.Sequential;
                ag.FailAndContinue = FailAndContinue; break;
            case ActionConfig a:
                a.Type = Enum.TryParse<ActionType>(ActionTypeText, out var at) ? at : ActionType.RunCommand;
                a.AgentName = AgentName; a.Command = Command; a.Parameters = Parameters;
                a.Timeout = Timeout; a.PollInterval = PollInterval;
                a.FailAndContinue = FailAndContinue; a.IsReboot = IsReboot;
                a.UserName = UserName; a.Password = Password;
                a.From = From; a.To = To; a.Title = Title; a.Body = Body;
                a.Attachment = Attachment; a.Embed = Embed; a.LargeFilesShare = LargeFilesShare; break;
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
            "WatchList" => $"WatchList  ({Children.Count} items)",
            "TemplateList" => $"Templates  ({Children.Count} templates)",
            "WatchItem" => !string.IsNullOrWhiteSpace(Tag) ? $"{Tag}  ({WatchPath}{Filter})" : $"{WatchPath}{Filter}",
            "Event" => $"Event: {EventType} ({ExecutionTypeText})",
            "ActionGroup" => $"[{ExecutionTypeText}] {Tag}",
            "Action" => $"{ActionTypeText}: {Command}",
            "Initialize" => $"Initialize: {ParameterFile}",
            "Ref" => $"Ref > {TemplateID}",
            "Template" => $"Template: {TemplateName}",
            _ => DisplayText,
        };
        if (NodeKind == "Action" && Enum.TryParse<ActionType>(ActionTypeText, out var t))
            NodeIcon = ResolveCommandIcon(Command, t);
    }

    // ── Helpers for finding tree nodes by model object ───────────────

    /// <summary>Find the TreeNodeViewModel whose ModelObject matches the given IActionNode.</summary>
    public TreeNodeViewModel? FindByModel(object model)
    {
        if (ReferenceEquals(ModelObject, model)) return this;
        foreach (var c in Children)
        {
            var found = c.FindByModel(model);
            if (found is not null) return found;
        }
        return null;
    }
}
