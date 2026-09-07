using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.ViewModels.WatchBuilder;

// =============================================================================
// WatchItem Builder — tree node view-models (Tier3 §5)
//
// Each node VM wraps ONE model object (WatchItemConfig / EventConfig /
// IActionNode) and edits it IN PLACE. The root VM serialises the same
// WatchListConfig via the existing WatchListXmlParser, so there is no bespoke
// XML string-building anywhere — the model is the single source of truth.
//
// Editing any [ObservableProperty] writes through to the model and bubbles an
// `Edited` signal to the root, which re-serialises the live XML pane and
// re-runs WatchListValidator.Analyze() for the inline validation panel.
// =============================================================================

/// <summary>The kinds of child a node can host, used to drive the Add toolbar.</summary>
public enum WatchNodeKind { WatchItem, Event, ActionGroup, Action, Initialize, Ref }

/// <summary>Base class for every node in the builder tree.</summary>
public abstract partial class WatchBuilderNodeViewModel : ObservableObject
{
    protected WatchBuilderNodeViewModel(WatchBuilderNodeViewModel? parent)
    {
        Parent = parent;
    }

    /// <summary>Parent node, or null for a root WatchItem.</summary>
    public WatchBuilderNodeViewModel? Parent { get; }

    public ObservableCollection<WatchBuilderNodeViewModel> Children { get; } = new();

    /// <summary>Invoked whenever this node (or one it forwards for) is edited.</summary>
    public Action? Edited { get; set; }

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Node kind — drives icons, Add rules and the detail template.</summary>
    public abstract WatchNodeKind Kind { get; }

    /// <summary>Short label shown in the tree.</summary>
    public abstract string Title { get; }

    /// <summary>Child kinds this node can host (empty = leaf).</summary>
    public virtual IReadOnlyList<WatchNodeKind> AddableKinds => [];

    /// <summary>Raise the edited signal and refresh the tree label.</summary>
    protected void RaiseEdited()
    {
        OnPropertyChanged(nameof(Title));
        // Bubble to the nearest handler (root wires only the top-level nodes, so
        // walk up until we find a subscriber).
        var node = this;
        while (node is not null)
        {
            if (node.Edited is { } handler) { handler(); return; }
            node = node.Parent;
        }
    }

    /// <summary>Wire the edited callback recursively so any descendant edit bubbles up.</summary>
    public void WireEdited(Action handler)
    {
        Edited = handler;
        foreach (var child in Children)
            child.WireEdited(handler);
    }

    /// <summary>Remove a child from both the VM tree and the underlying model.</summary>
    public abstract void RemoveChild(WatchBuilderNodeViewModel child);

    /// <summary>Add a new child of the given kind; returns the created VM or null.</summary>
    public virtual WatchBuilderNodeViewModel? AddChild(WatchNodeKind kind) => null;
}

// ── WatchItem ────────────────────────────────────────────────────────────────
public sealed partial class WatchItemNodeViewModel : WatchBuilderNodeViewModel
{
    private readonly WatchItemConfig _model;

    public WatchItemNodeViewModel(WatchItemConfig model, WatchBuilderNodeViewModel? parent)
        : base(parent)
    {
        _model = model;
        _tag = model.Tag;
        _path = model.Path;
        _filter = model.Filter;
        _isEnabled = model.IsEnabled;
        _buildNumberField = model.BuildNumberField;
        _dropLocationField = model.DropLocationField;
        _buildBasePath = model.BuildBasePath;

        foreach (var ev in model.Events)
            Children.Add(new EventNodeViewModel(ev, this));
    }

    public WatchItemConfig Model => _model;
    public override WatchNodeKind Kind => WatchNodeKind.WatchItem;
    public override string Title => string.IsNullOrWhiteSpace(Tag) ? "(unnamed WatchItem)" : Tag;
    public override IReadOnlyList<WatchNodeKind> AddableKinds => [WatchNodeKind.Event];

    [ObservableProperty] private string _tag;
    [ObservableProperty] private string _path;
    [ObservableProperty] private string _filter;
    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private string _buildNumberField;
    [ObservableProperty] private string _dropLocationField;
    [ObservableProperty] private string _buildBasePath;

    partial void OnTagChanged(string value) { _model.Tag = value; RaiseEdited(); }
    partial void OnPathChanged(string value) { _model.Path = value; RaiseEdited(); }
    partial void OnFilterChanged(string value) { _model.Filter = value; RaiseEdited(); }
    partial void OnIsEnabledChanged(bool value) { _model.IsEnabled = value; RaiseEdited(); }
    partial void OnBuildNumberFieldChanged(string value) { _model.BuildNumberField = value; RaiseEdited(); }
    partial void OnDropLocationFieldChanged(string value) { _model.DropLocationField = value; RaiseEdited(); }
    partial void OnBuildBasePathChanged(string value) { _model.BuildBasePath = value; RaiseEdited(); }

    public override WatchBuilderNodeViewModel? AddChild(WatchNodeKind kind)
    {
        if (kind != WatchNodeKind.Event) return null;
        var ev = new EventConfig();
        _model.Events.Add(ev);
        var vm = new EventNodeViewModel(ev, this);
        Children.Add(vm);
        RaiseEdited();
        return vm;
    }

    public override void RemoveChild(WatchBuilderNodeViewModel child)
    {
        if (child is EventNodeViewModel ev)
        {
            _model.Events.Remove(ev.Model);
            Children.Remove(child);
            RaiseEdited();
        }
    }
}

// ── Event ────────────────────────────────────────────────────────────────────
public sealed partial class EventNodeViewModel : WatchBuilderNodeViewModel
{
    private readonly EventConfig _model;

    public EventNodeViewModel(EventConfig model, WatchBuilderNodeViewModel? parent)
        : base(parent)
    {
        _model = model;
        _type = model.Type;
        _executionType = model.ExecutionType;

        foreach (var node in model.Children)
            Children.Add(ActionNodeFactory.Create(node, this));
    }

    public EventConfig Model => _model;
    public override WatchNodeKind Kind => WatchNodeKind.Event;
    public override string Title => $"Event: {Type} ({ExecutionType})";

    public override IReadOnlyList<WatchNodeKind> AddableKinds =>
        [WatchNodeKind.ActionGroup, WatchNodeKind.Action, WatchNodeKind.Initialize, WatchNodeKind.Ref];

    public IReadOnlyList<string> EventTypes { get; } = ["Renamed", "Created", "Changed"];

    [ObservableProperty] private string _type;
    [ObservableProperty] private ExecutionMode _executionType;

    partial void OnTypeChanged(string value) { _model.Type = value; RaiseEdited(); }
    partial void OnExecutionTypeChanged(ExecutionMode value) { _model.ExecutionType = value; RaiseEdited(); }

    public override WatchBuilderNodeViewModel? AddChild(WatchNodeKind kind)
    {
        var node = ActionNodeFactory.NewModel(kind);
        if (node is null) return null;
        _model.Children.Add(node);
        var vm = ActionNodeFactory.Create(node, this);
        Children.Add(vm);
        RaiseEdited();
        return vm;
    }

    public override void RemoveChild(WatchBuilderNodeViewModel child)
    {
        if (child is IActionNodeHost host)
        {
            _model.Children.Remove(host.NodeModel);
            Children.Remove(child);
            RaiseEdited();
        }
    }
}

/// <summary>Marker so a parent can pull the underlying model out of a child action node VM.</summary>
public interface IActionNodeHost
{
    IActionNode NodeModel { get; }
}

// ── ActionGroup ───────────────────────────────────────────────────────────────
public sealed partial class ActionGroupNodeViewModel : WatchBuilderNodeViewModel, IActionNodeHost
{
    private readonly ActionGroupConfig _model;

    public ActionGroupNodeViewModel(ActionGroupConfig model, WatchBuilderNodeViewModel? parent)
        : base(parent)
    {
        _model = model;
        _tag = model.Tag;
        _executionType = model.ExecutionType;
        _failAndContinue = model.FailAndContinue;
        _skip = model.Skip;
        _skipReason = model.SkipReason ?? "";
        _comment = model.Comment ?? "";

        foreach (var node in model.Children)
            Children.Add(ActionNodeFactory.Create(node, this));
    }

    public IActionNode NodeModel => _model;
    public override WatchNodeKind Kind => WatchNodeKind.ActionGroup;
    public override string Title => string.IsNullOrWhiteSpace(Tag) ? "(unnamed group)" : $"Group: {Tag}";

    public override IReadOnlyList<WatchNodeKind> AddableKinds =>
        [WatchNodeKind.ActionGroup, WatchNodeKind.Action, WatchNodeKind.Initialize, WatchNodeKind.Ref];

    [ObservableProperty] private string _tag;
    [ObservableProperty] private ExecutionMode _executionType;
    [ObservableProperty] private bool _failAndContinue;
    [ObservableProperty] private bool _skip;
    [ObservableProperty] private string _skipReason;
    [ObservableProperty] private string _comment;

    partial void OnTagChanged(string value) { _model.Tag = value; RaiseEdited(); }
    partial void OnExecutionTypeChanged(ExecutionMode value) { _model.ExecutionType = value; RaiseEdited(); }
    partial void OnFailAndContinueChanged(bool value) { _model.FailAndContinue = value; RaiseEdited(); }
    partial void OnSkipChanged(bool value) { _model.Skip = value; RaiseEdited(); }
    partial void OnSkipReasonChanged(string value) { _model.SkipReason = string.IsNullOrWhiteSpace(value) ? null : value; RaiseEdited(); }
    partial void OnCommentChanged(string value) { _model.Comment = string.IsNullOrWhiteSpace(value) ? null : value; RaiseEdited(); }

    public override WatchBuilderNodeViewModel? AddChild(WatchNodeKind kind)
    {
        var node = ActionNodeFactory.NewModel(kind);
        if (node is null) return null;
        _model.Children.Add(node);
        var vm = ActionNodeFactory.Create(node, this);
        Children.Add(vm);
        RaiseEdited();
        return vm;
    }

    public override void RemoveChild(WatchBuilderNodeViewModel child)
    {
        if (child is IActionNodeHost host)
        {
            _model.Children.Remove(host.NodeModel);
            Children.Remove(child);
            RaiseEdited();
        }
    }
}

// ── Action ────────────────────────────────────────────────────────────────────
public sealed partial class ActionNodeViewModel : WatchBuilderNodeViewModel, IActionNodeHost
{
    private readonly ActionConfig _model;

    public ActionNodeViewModel(ActionConfig model, WatchBuilderNodeViewModel? parent)
        : base(parent)
    {
        _model = model;
        _type = model.Type;
        _tag = model.Tag;
        _agentName = model.AgentName;
        _command = model.Command;
        _parameters = model.Parameters;
        _timeout = model.Timeout;
        _pollInterval = model.PollInterval;
        _failAndContinue = model.FailAndContinue;
        _skip = model.Skip;
        _skipReason = model.SkipReason ?? "";
        _comment = model.Comment ?? "";
        _isReboot = model.IsReboot;
        _userName = model.UserName;
        _password = model.Password;
        _from = model.From;
        _to = model.To;
        _emailTitle = model.Title;
        _body = model.Body;
        _attachment = model.Attachment;
        _embed = model.Embed;
        _largeFilesShare = model.LargeFilesShare;
    }

    public IActionNode NodeModel => _model;
    public override WatchNodeKind Kind => WatchNodeKind.Action;

    public override string Title
    {
        get
        {
            var label = !string.IsNullOrWhiteSpace(Tag) ? Tag : _model.ResolvedTag;
            return $"{Type}: {label}";
        }
    }

    public static IReadOnlyList<ActionType> ActionTypes { get; } =
        [ActionType.RunCommand, ActionType.RunRemoteCommand, ActionType.SendMail];

    [ObservableProperty] private ActionType _type;
    [ObservableProperty] private string _tag;
    [ObservableProperty] private string _agentName;
    [ObservableProperty] private string _command;
    [ObservableProperty] private string _parameters;
    [ObservableProperty] private int _timeout;
    [ObservableProperty] private int _pollInterval;
    [ObservableProperty] private bool _failAndContinue;
    [ObservableProperty] private bool _skip;
    [ObservableProperty] private string _skipReason;
    [ObservableProperty] private string _comment;
    [ObservableProperty] private bool _isReboot;
    [ObservableProperty] private string _userName;
    [ObservableProperty] private string _password;
    [ObservableProperty] private string _from;
    [ObservableProperty] private string _to;
    [ObservableProperty] private string _emailTitle;
    [ObservableProperty] private string _body;
    [ObservableProperty] private string _attachment;
    [ObservableProperty] private string _embed;
    [ObservableProperty] private string _largeFilesShare;

    /// <summary>True when the RunRemoteCommand-only fields should show.</summary>
    public bool IsRemoteCommand => Type == ActionType.RunRemoteCommand;
    /// <summary>True when the command/parameter fields apply (RunCommand or RunRemoteCommand).</summary>
    public bool IsCommand => Type is ActionType.RunCommand or ActionType.RunRemoteCommand;
    /// <summary>True when the SendMail-only fields should show.</summary>
    public bool IsSendMail => Type == ActionType.SendMail;

    partial void OnTypeChanged(ActionType value)
    {
        _model.Type = value;
        OnPropertyChanged(nameof(IsRemoteCommand));
        OnPropertyChanged(nameof(IsCommand));
        OnPropertyChanged(nameof(IsSendMail));
        RaiseEdited();
    }

    partial void OnTagChanged(string value) { _model.Tag = value; RaiseEdited(); }
    partial void OnAgentNameChanged(string value) { _model.AgentName = value; RaiseEdited(); }
    partial void OnCommandChanged(string value) { _model.Command = value; RaiseEdited(); }
    partial void OnParametersChanged(string value) { _model.Parameters = value; RaiseEdited(); }
    partial void OnTimeoutChanged(int value) { _model.Timeout = value; RaiseEdited(); }
    partial void OnPollIntervalChanged(int value) { _model.PollInterval = value; RaiseEdited(); }
    partial void OnFailAndContinueChanged(bool value) { _model.FailAndContinue = value; RaiseEdited(); }
    partial void OnSkipChanged(bool value) { _model.Skip = value; RaiseEdited(); }
    partial void OnSkipReasonChanged(string value) { _model.SkipReason = string.IsNullOrWhiteSpace(value) ? null : value; RaiseEdited(); }
    partial void OnCommentChanged(string value) { _model.Comment = string.IsNullOrWhiteSpace(value) ? null : value; RaiseEdited(); }
    partial void OnIsRebootChanged(bool value) { _model.IsReboot = value; RaiseEdited(); }
    partial void OnUserNameChanged(string value) { _model.UserName = value; RaiseEdited(); }
    partial void OnPasswordChanged(string value) { _model.Password = value; RaiseEdited(); }
    partial void OnFromChanged(string value) { _model.From = value; RaiseEdited(); }
    partial void OnToChanged(string value) { _model.To = value; RaiseEdited(); }
    partial void OnEmailTitleChanged(string value) { _model.Title = value; RaiseEdited(); }
    partial void OnBodyChanged(string value) { _model.Body = value; RaiseEdited(); }
    partial void OnAttachmentChanged(string value) { _model.Attachment = value; RaiseEdited(); }
    partial void OnEmbedChanged(string value) { _model.Embed = value; RaiseEdited(); }
    partial void OnLargeFilesShareChanged(string value) { _model.LargeFilesShare = value; RaiseEdited(); }

    public override void RemoveChild(WatchBuilderNodeViewModel child) { /* leaf */ }
}

// ── Initialize ────────────────────────────────────────────────────────────────
public sealed partial class InitializeNodeViewModel : WatchBuilderNodeViewModel, IActionNodeHost
{
    private readonly InitializeConfig _model;

    public InitializeNodeViewModel(InitializeConfig model, WatchBuilderNodeViewModel? parent)
        : base(parent)
    {
        _model = model;
        _tag = model.Tag;
        _parameterFile = model.ParameterFile;
    }

    public IActionNode NodeModel => _model;
    public override WatchNodeKind Kind => WatchNodeKind.Initialize;
    public override string Title =>
        $"Initialize: {(string.IsNullOrWhiteSpace(ParameterFile) ? "(no file)" : System.IO.Path.GetFileName(ParameterFile))}";

    [ObservableProperty] private string _tag;
    [ObservableProperty] private string _parameterFile;

    partial void OnTagChanged(string value) { _model.Tag = value; RaiseEdited(); }
    partial void OnParameterFileChanged(string value) { _model.ParameterFile = value; RaiseEdited(); }

    public override void RemoveChild(WatchBuilderNodeViewModel child) { /* leaf */ }
}

// ── Ref ───────────────────────────────────────────────────────────────────────
public sealed partial class RefNodeViewModel : WatchBuilderNodeViewModel, IActionNodeHost
{
    private readonly RefConfig _model;

    public RefNodeViewModel(RefConfig model, WatchBuilderNodeViewModel? parent)
        : base(parent)
    {
        _model = model;
        _templateID = model.TemplateID;
    }

    public IActionNode NodeModel => _model;
    public override WatchNodeKind Kind => WatchNodeKind.Ref;
    public override string Title =>
        $"Ref → {(string.IsNullOrWhiteSpace(TemplateID) ? "(unset)" : TemplateID)}";

    [ObservableProperty] private string _templateID;

    partial void OnTemplateIDChanged(string value) { _model.TemplateID = value; RaiseEdited(); }

    public override void RemoveChild(WatchBuilderNodeViewModel child) { /* leaf */ }
}

// ── Factory ─────────────────────────────────────────────────────────────────
internal static class ActionNodeFactory
{
    public static WatchBuilderNodeViewModel Create(IActionNode node, WatchBuilderNodeViewModel parent) => node switch
    {
        ActionGroupConfig g => new ActionGroupNodeViewModel(g, parent),
        ActionConfig a => new ActionNodeViewModel(a, parent),
        InitializeConfig i => new InitializeNodeViewModel(i, parent),
        RefConfig r => new RefNodeViewModel(r, parent),
        _ => throw new NotSupportedException($"Unknown node type {node.GetType().Name}"),
    };

    public static IActionNode? NewModel(WatchNodeKind kind) => kind switch
    {
        WatchNodeKind.ActionGroup => new ActionGroupConfig { Tag = "New group" },
        WatchNodeKind.Action => new ActionConfig { Type = ActionType.RunRemoteCommand, Tag = "New action" },
        WatchNodeKind.Initialize => new InitializeConfig { Tag = "InitializeParams" },
        WatchNodeKind.Ref => new RefConfig(),
        _ => null,
    };
}
