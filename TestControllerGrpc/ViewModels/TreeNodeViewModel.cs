using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels;

/// <summary>Per-pipeline permission state for tree row visual treatment.</summary>
public enum PipelinePermissionState
{
    /// <summary>User can trigger this pipeline (Admin = all; Engineer = assigned).</summary>
    Triggerable,
    /// <summary>Visible but user cannot trigger (not assigned / Guest).</summary>
    ViewOnly,
}

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

    /// <summary>Read-only mirror of a Ref'd template. Never written back to the model.</summary>
    [ObservableProperty] private bool _isRefExpansion;

    // ── Phase 3b: Pipeline lock badge (per-row, bound in TreeViewSpec.xaml) ──
    [ObservableProperty] private LockBadgeViewModel? _lockBadge;

    // ── Resolved display values ─────────────────────────────────────
    // Every one of these is a thin wrapper over Describe(); none carries its own resolving logic.

    public string ResolvedParameters => Describe(Parameters).Text;
    public string ResolvedCommand => Describe(Command).Text;
    public string ResolvedAgentName => Describe(AgentName).Text;
    public string ResolvedTag => Describe(Tag).Text;
    public string ResolvedTo => Describe(To).Text;
    public string ResolvedTitle => Describe(Title).Text;
    public string ResolvedBody => Describe(Body).Text;

    // ── Resolved vs unresolved ──────────────────────────────────────
    // Raw tokens used to render in the same green as real values, so "[_Installer]" read as a
    // resolved path. Unresolved tokens now carry their own "(not set)" marker inline, so a field
    // that resolves three of four tokens still shows the three real values.

    /// <summary>Shown instead of the tokens when the node has no pipeline context at all.</summary>
    public const string UnresolvedHint = TokenDisplay.NoContextHint;

    /// <summary>A Library node whose template has no chosen pipeline can resolve nothing.</summary>
    public bool HasNoTokenContext => TokenScope is null;

    public bool IsCommandResolved => !Describe(Command).HasUnresolved;
    public bool IsParametersResolved => !Describe(Parameters).HasUnresolved;
    public bool IsAgentNameResolved => !Describe(AgentName).HasUnresolved;

    public string ResolvedCommandDisplay => FieldDisplay(Command);
    public string ResolvedParametersDisplay => FieldDisplay(Parameters);
    public string ResolvedAgentNameDisplay => FieldDisplay(AgentName);

    /// <summary>
    /// Node Properties text. With no context at all, say so ONCE rather than repeating "(not set)"
    /// against every token the node could never have resolved.
    /// </summary>
    private string FieldDisplay(string? raw)
        => HasNoTokenContext && !string.IsNullOrWhiteSpace(raw) && raw.Contains('[')
            ? TokenDisplay.NoContextHint
            : Describe(raw).Text;

    /// <summary>Tooltip lines "[_Token] -> value (Layer)" for every token in the given fields.</summary>
    public string TokenTooltip(params string?[] fields)
    {
        var lines = fields
            .SelectMany(f => Describe(f).Describe())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return lines.Count == 0 ? "" : string.Join(Environment.NewLine, lines);
    }

    /// <summary>Provenance for the fields the tree tooltip shows, so a value's layer is visible.</summary>
    public string TokenProvenance => TokenTooltip(Command, Parameters, AgentName);

    private void NotifyResolvedChanged()
    {
        OnPropertyChanged(nameof(ResolvedParameters));
        OnPropertyChanged(nameof(ResolvedCommand));
        OnPropertyChanged(nameof(ResolvedAgentName));
        OnPropertyChanged(nameof(ResolvedTag));
        OnPropertyChanged(nameof(ResolvedTo));
        OnPropertyChanged(nameof(ResolvedTitle));
        OnPropertyChanged(nameof(ResolvedBody));
        OnPropertyChanged(nameof(IsCommandResolved));
        OnPropertyChanged(nameof(IsParametersResolved));
        OnPropertyChanged(nameof(IsAgentNameResolved));
        OnPropertyChanged(nameof(ResolvedCommandDisplay));
        OnPropertyChanged(nameof(ResolvedParametersDisplay));
        OnPropertyChanged(nameof(ResolvedAgentNameDisplay));
        OnPropertyChanged(nameof(HasNoTokenContext));
        OnPropertyChanged(nameof(TokenProvenance));
    }

    /// <summary>Called by source generator when Parameters changes — refreshes ResolvedParameters.</summary>
    partial void OnParametersChanged(string value) => NotifyResolvedChanged();

    /// <summary>Called by source generator when Command changes — refreshes ResolvedCommand.</summary>
    partial void OnCommandChanged(string value) => NotifyResolvedChanged();

    /// <summary>Called by source generator when AgentName changes — refreshes ResolvedAgentName.</summary>
    partial void OnAgentNameChanged(string value) => NotifyResolvedChanged();

    // ── Skip state ────────────────────────────────────────────────
    // Skip lives on the model (ISkippableNode); these mirror it so the row can bind. SkipOrigin is NOT on the
    // model by design - it is a traversal result, so it is recomputed top-down whenever the flag changes.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSkippedAnyway))]
    private bool _skip;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSkipReason))]
    private string _skipReason = "";

    /// <summary>Keeps the reason label out of the row when the user skipped without typing one.</summary>
    public bool HasSkipReason => !string.IsNullOrWhiteSpace(SkipReason);

    /// <summary>Explicit = this node carries the flag; Inherited = an ancestor does.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSkippedAnyway))]
    [NotifyPropertyChangedFor(nameof(IsSkippedExplicit))]
    [NotifyPropertyChangedFor(nameof(IsSkippedByParent))]
    private SkipOrigin _skipOrigin = SkipOrigin.None;

    /// <summary>True for an explicitly skipped node AND for everything beneath it.</summary>
    public bool IsSkippedAnyway => SkipOrigin != SkipOrigin.None;

    /// <summary>Carries the pill and the reason. Bound as a bool so XAML needs no cross-assembly enum ref.</summary>
    public bool IsSkippedExplicit => SkipOrigin == SkipOrigin.Explicit;

    /// <summary>Dimmed and labelled "via parent", but deliberately no pill - only one node owns the decision.</summary>
    public bool IsSkippedByParent => SkipOrigin == SkipOrigin.Inherited;

    /// <summary>Skipped children counted for the parent's "N actions - X skipped" meta.</summary>
    public int SkippedChildCount => Children.Count(c => c.IsSkippedAnyway);

    private bool _syncingSkipFromModel;

    partial void OnSkipChanged(bool value)
    {
        if (_syncingSkipFromModel) return;
        if (ModelObject is ISkippableNode skippable)
            skippable.Skip = value;
        RootOf(this).RecomputeSkipOrigins(SkipState.NotSkipped);
    }

    partial void OnSkipReasonChanged(string value)
    {
        if (_syncingSkipFromModel) return;
        if (ModelObject is ISkippableNode skippable)
            skippable.SkipReason = string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static TreeNodeViewModel RootOf(TreeNodeViewModel node)
    {
        while (node.Parent is { } p) node = p;
        return node;
    }

    /// <summary>
    /// Walks the subtree stamping <see cref="SkipOrigin"/>, reusing <see cref="SkipEvaluator.Descend"/> so the
    /// tree and the executor can never disagree about who is responsible for a skip.
    /// </summary>
    public void RecomputeSkipOrigins(SkipState inherited)
    {
        var skippable = ModelObject as ISkippableNode;

        SkipState state =
            inherited.IsSkipped ? inherited
            : skippable is { Skip: true } ? new SkipState(SkipOrigin.Explicit, skippable.SkipReason, null)
            : SkipState.NotSkipped;

        SkipOrigin = state.Origin;
        if (skippable is not null)
        {
            // Mirror the model without re-entering the cascade this call is already performing.
            _syncingSkipFromModel = true;
            try
            {
                Skip = skippable.Skip;
                SkipReason = skippable.SkipReason ?? "";
            }
            finally { _syncingSkipFromModel = false; }
        }
        SkipBlame = state.SkippedByNode ?? "";

        SkipState childInherited = skippable is null
            ? state
            : SkipEvaluator.Descend(state, skippable, DescribeForBlame());

        foreach (var child in Children)
            child.RecomputeSkipOrigins(childInherited);

        OnPropertyChanged(nameof(SkippedChildCount));
    }

    /// <summary>Label of the ancestor that must be un-skipped; empty when this node is the one.</summary>
    [ObservableProperty] private string _skipBlame = "";

    private string DescribeForBlame() => NodeKind switch
    {
        NodeKinds.ActionGroup => $"group '{Tag}'",
        NodeKinds.Action      => $"action '{Tag}'",
        _                     => string.IsNullOrWhiteSpace(Tag) ? NodeKind : $"'{Tag}'",
    };

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

    // ── Pipeline permission state (Phase 2b: reads open, trigger gated) ──
    [ObservableProperty] private PipelinePermissionState _pipelinePermission = PipelinePermissionState.Triggerable;
    /// <summary>True when in Secured mode and the permission pill should be visible on WatchItem rows.</summary>
    [ObservableProperty] private bool _showPermissionIndicator;

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
            case "Skipped":
                // Grey, never red: a skipped node is a deliberate choice, not a failure.
                StatusSymbol = "\u2212";  // − (matches ActionPillVM)
                StatusColor = "#FF9399B2"; // Gray
                StatusTooltip = string.IsNullOrWhiteSpace(SkipReason)
                    ? "Skipped"
                    : $"Skipped: {SkipReason}";
                FailureMessage = "";
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
    /// <remarks>
    /// Callers paint a whole subtree "Running" optimistically before dispatch. A skipped node is never going
    /// to run, so it must not be given a spinner - it reports Skipped from the outset instead.
    /// </remarks>
    public void SetStatusRecursive(string status)
    {
        ExecutionStatus = status == "Running" && IsSkippedAnyway ? "Skipped" : status;
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
        var hasSkipped = false;

        foreach (var child in parent.Children)
        {
            switch (child.ExecutionStatus)
            {
                case "Failed" or "PartialFailure": hasFailed = true; break;
                case "Running": hasRunning = true; break;
                case "Success": hasSuccess = true; break;
                case "Cancelled": hasCancelled = true; break;
                case "Skipped": hasSkipped = true; break;
            }
        }

        if (hasRunning) return "Running";
        if (hasFailed && hasSuccess) return "PartialFailure";
        if (hasFailed) return "Failed";
        if (hasCancelled && hasSuccess) return "PartialFailure";
        if (hasCancelled) return "Cancelled";
        if (hasSuccess) return "Success";
        // Only when nothing ran: a skipped sibling must never downgrade a successful group, but without
        // this a skipped group would be reset to Idle by its own children propagating up.
        if (hasSkipped) return "Skipped";
        return "Idle";
    }

    public ObservableCollection<TreeNodeViewModel> Children { get; } = new();
    public object? ModelObject { get; set; }
    public TreeNodeViewModel? Parent { get; set; }

    /// <summary>
    /// Expands every ancestor so this node is reachable in the tree. Combined with
    /// <see cref="Views.Behaviors.TreeViewItemBehavior"/>, selecting a node (e.g. after
    /// creation or a search jump) also scrolls it into view.
    /// </summary>
    public void ExpandAncestors()
    {
        for (var p = Parent; p is not null; p = p.Parent)
            p.IsExpanded = true;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) ExpandAncestors();
    }

    // ── Token resolution for display ────────────────────────────────

    private static readonly Regex TokenPattern = new(@"\[(\w+)\]", RegexOptions.Compiled);

    /// <summary>Scope holding values every pipeline inherits (the global variables file).</summary>
    public const string SharedScope = "";

    /// <summary>
    /// Token values per pipeline, keyed by WatchItem Tag, plus <see cref="SharedScope"/>.
    /// </summary>
    /// <remarks>
    /// This was ONE flat dictionary serving every pipeline, so whichever WatchItem loaded last won
    /// each key. A per-pipeline [_EmailCheck] previewed as some other pipeline's address, and a
    /// Templates-library node previewed fully resolved values it would never actually receive.
    /// </remarks>
    private static readonly Dictionary<string, Dictionary<string, string>> TokenScopes =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Token values for one pipeline, created on first use.</summary>
    public static Dictionary<string, string> TokensFor(string? scope)
    {
        var key = scope ?? SharedScope;
        if (!TokenScopes.TryGetValue(key, out var values))
            TokenScopes[key] = values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return values;
    }

    /// <summary>
    /// Layer each token's value came from, parallel to <see cref="TokenScopes"/>. Kept separate so
    /// the existing dictionary API still works; values written without a layer read back as
    /// <see cref="TokenLayer.Unknown"/> and are simply not attributed in tooltips.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, TokenLayer>> TokenLayers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Writes a token together with the parameter layer it came from.</summary>
    public static void SetToken(string? scope, string key, string value, TokenLayer layer)
    {
        var s = scope ?? SharedScope;
        TokensFor(s)[key] = value;
        if (!TokenLayers.TryGetValue(s, out var layers))
            TokenLayers[s] = layers = new Dictionary<string, TokenLayer>(StringComparer.OrdinalIgnoreCase);
        layers[key] = layer;
    }

    private static TokenLayer LayerFor(string scope, string key, TokenLayer fallback = TokenLayer.Unknown)
    {
        if (!TokenLayers.TryGetValue(scope, out var layers)) return fallback;
        if (layers.TryGetValue(key, out var layer)) return layer;
        if (key.StartsWith('_') && layers.TryGetValue(key[1..], out layer)) return layer;
        return fallback;
    }

    /// <summary>
    /// Toolbar toggle: show the authored tokens instead of their values. Display-only - it never
    /// affects what is saved or executed.
    /// </summary>
    public static bool ShowRawTokens { get; set; }

    /// <summary>Values every pipeline inherits.</summary>
    public static Dictionary<string, string> SharedTokens => TokensFor(SharedScope);

    public static void ClearTokenScopes()
    {
        TokenScopes.Clear();
        TokenLayers.Clear();
    }

    /// <summary>Values and layers for every scope, taken before a reload that might fail.</summary>
    public sealed record TokenSnapshot(
        Dictionary<string, Dictionary<string, string>> Values,
        Dictionary<string, Dictionary<string, TokenLayer>> Layers);

    /// <summary>
    /// Copies the current token state so a reload can be rolled back per pipeline. A parameter file
    /// caught mid-save parses as garbage; without this the clear-then-reload would leave every label
    /// reading "(not set)" until the next successful edit.
    /// </summary>
    public static TokenSnapshot SnapshotTokens() => new(
        TokenScopes.ToDictionary(
            e => e.Key,
            e => new Dictionary<string, string>(e.Value, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase),
        TokenLayers.ToDictionary(
            e => e.Key,
            e => new Dictionary<string, TokenLayer>(e.Value, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase));

    /// <summary>Puts one scope back to its snapshot values, leaving every other scope alone.</summary>
    public static void RestoreScopeFrom(TokenSnapshot snapshot, string? scope)
    {
        var key = scope ?? SharedScope;

        if (snapshot.Values.TryGetValue(key, out var values))
            TokenScopes[key] = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        else
            TokenScopes.Remove(key);

        if (snapshot.Layers.TryGetValue(key, out var layers))
            TokenLayers[key] = new Dictionary<string, TokenLayer>(layers, StringComparer.OrdinalIgnoreCase);
        else
            TokenLayers.Remove(key);
    }

    // ── Values actually used by a run ───────────────────────────────
    // The preview dictionary and the execution dictionary are two independent populations of the
    // same data, so a pipeline that ran an hour ago could preview values it never received. When a
    // session is known for a pipeline, its own ResolvedParameters win.

    private static readonly Dictionary<string, (string SessionId, Dictionary<string, string> Values)> SessionScopes =
        new(StringComparer.OrdinalIgnoreCase);

    public static void SetSessionValues(string? pipelineTag, string sessionId, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrWhiteSpace(pipelineTag)) return;
        SessionScopes[pipelineTag] = (sessionId, new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase));
    }

    public static void ClearSessionValues() => SessionScopes.Clear();

    /// <summary>Run whose values this node is showing, or null when it is a plain preview.</summary>
    public static string? SessionIdFor(string? scope) =>
        scope is not null && SessionScopes.TryGetValue(scope, out var s) ? s.SessionId : null;

    public string? ValuesFromSessionId => SessionIdFor(TokenScope);

    /// <summary>Banner text for the properties panel when the values came from a real run.</summary>
    public string ValuesSourceLabel =>
        ValuesFromSessionId is { } id ? $"Values used in run {id}" : "";

    public bool HasSessionValues => ValuesFromSessionId is not null;

    /// <summary>
    /// Pipeline whose values this node's preview resolves against, or null when nothing may be
    /// resolved. A Templates-library node resolves nothing until a pipeline context is chosen for
    /// its template: a template has no settings of its own, so any value shown before that is a
    /// guess at which pipeline will eventually run it.
    /// </summary>
    public string? TokenScope
    {
        get
        {
            for (var n = this; n is not null; n = n.Parent)
            {
                if (n.ModelObject is WatchItemConfig wi) return wi.Tag;
                if (n.NodeKind == NodeKinds.Template) return TemplateRunContext.For(n.Tag);
                if (n.NodeKind == NodeKinds.TemplateList) return null;
            }
            return SharedScope;
        }
    }

    /// <summary>Template this node belongs to, or null when it is in the WatchList tree.</summary>
    public string? OwningTemplateId
    {
        get
        {
            for (var n = this; n is not null; n = n.Parent)
            {
                if (n.NodeKind == NodeKinds.WatchItem) return null;
                if (n.NodeKind == NodeKinds.Template) return n.Tag;
            }
            return null;
        }
    }

    /// <summary>Chip shown on a Template header row once a pipeline context is chosen.</summary>
    public string TemplateContextLabel
    {
        get
        {
            if (NodeKind != NodeKinds.Template) return "";
            if (TemplateRunContext.For(Tag) is not { } tag) return "";
            return $"Context: {tag}{TemplateContextSuffix}";
        }
    }

    /// <summary>Pipeline part of the chip. Trimmed first, because the suffix carries the meaning.</summary>
    public string TemplateContextPipeline =>
        NodeKind == NodeKinds.Template && TemplateRunContext.For(Tag) is { } tag ? $"Context: {tag}" : "";

    /// <summary>How the context was decided. Rendered separately so ellipsis can never eat it.</summary>
    public string TemplateContextSuffix =>
        NodeKind != NodeKinds.Template || TemplateRunContext.For(Tag) is null
            ? ""
            : TemplateRunContext.SourceFor(Tag) switch
            {
                TemplateContextSource.Running => " (running)",
                TemplateContextSource.Auto => " (auto)",
                _ => "",
            };

    public bool HasTemplateContext => TemplateContextLabel.Length > 0;

    /// <summary>Resolves [Token] placeholders against this node's pipeline.</summary>
    public string ResolveTokens(string input) => ResolveTokens(input, TokenScope);

    /// <summary>
    /// Resolves [Token] placeholders using <paramref name="scope"/>'s values, falling back to the
    /// shared scope. A null scope resolves nothing, leaving the tokens visible.
    /// </summary>
    public static string ResolveTokens(string input, string? scope)
        => Describe(input, scope).Text;

    /// <summary>
    /// Full resolution result for a field: display text plus every token with its value and layer.
    /// This is the single entry point - <see cref="ResolveTokens(string)"/> and every Resolved*
    /// property are thin wrappers over it, so no call site carries its own resolving logic.
    /// </summary>
    public static DisplayResult Describe(string? input, string? scope)
        => Describe(input, scope, null);

    private static DisplayResult Describe(string? input, string? scope, Func<string, bool>? isReserved)
    {
        // A null scope means "nothing may be resolved here" (a Library node with no context),
        // which is different from "resolved to nothing".
        if (scope is null) return new DisplayResult(input ?? "", []);
        return TokenDisplay.Resolve(input, name => LookupToken(name, scope), ShowRawTokens, isReserved);
    }

    /// <summary>
    /// "[Sequential]" / "[Parallel]" is execution-mode decoration baked into DisplayText, not a
    /// token. Treating it as one marked it "(not set)", and the label converter then stripped the
    /// brackets and left a bare "(not set)" in front of every group name.
    /// </summary>
    private static bool IsExecutionModeDecoration(string name)
        => name.Equals("Sequential", StringComparison.OrdinalIgnoreCase)
           || name.Equals("Parallel", StringComparison.OrdinalIgnoreCase);

    /// <summary>Per-node overload using this node's own scope.</summary>
    public DisplayResult Describe(string? input) => Describe(input, TokenScope);

    /// <summary>
    /// Value and layer for one token. A value the run actually used outranks anything the editor
    /// would predict, so the session layer is consulted first.
    /// </summary>
    private static (string Value, TokenLayer Layer)? LookupToken(string name, string scope)
    {
        SessionScopes.TryGetValue(scope, out var session);
        if (session.Values is not null && TryGet(session.Values, name, out var used))
            return (used, TokenLayer.Run);

        if (TryGet(TokensFor(scope), name, out var own))
            return (own, LayerFor(scope, name));

        if (TryGet(SharedTokens, name, out var shared))
            return (shared, LayerFor(SharedScope, name, TokenLayer.Global));

        return null;

        static bool TryGet(Dictionary<string, string> values, string key, out string value)
        {
            if (values.TryGetValue(key, out value!)) return true;
            // Both [BuildNumber] and [_BuildNumber] must resolve.
            if (key.StartsWith('_') && values.TryGetValue(key[1..], out value!)) return true;
            return false;
        }
    }

    /// <summary>Called by source generator when DisplayText changes.</summary>
    partial void OnDisplayTextChanged(string value)
    {
        UpdateResolvedText(value);
    }

    /// <summary>Updates ResolvedDisplayText and unresolved-token metadata.</summary>
    private void UpdateResolvedText(string rawText)
    {
        var result = Describe(rawText, TokenScope, IsExecutionModeDecoration);
        ResolvedDisplayText = result.Text;

        HasUnresolvedTokens = result.HasUnresolved;
        UnresolvedTokenTooltip = result.HasUnresolved
            ? string.Join(Environment.NewLine, result.Describe())
            : "";
    }

    /// <summary>Refreshes resolved display text on this node and all descendants (e.g. after token dict changes).</summary>
    public void RefreshResolvedTextRecursive()
    {
        UpdateResolvedText(DisplayText);
        NotifyResolvedChanged();
        OnPropertyChanged(nameof(TokenScope));
        OnPropertyChanged(nameof(TemplateContextLabel));
        OnPropertyChanged(nameof(TemplateContextPipeline));
        OnPropertyChanged(nameof(TemplateContextSuffix));
        OnPropertyChanged(nameof(HasTemplateContext));
        OnPropertyChanged(nameof(ValuesFromSessionId));
        OnPropertyChanged(nameof(ValuesSourceLabel));
        OnPropertyChanged(nameof(HasSessionValues));
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

        var templates = TemplateIndex(config.Templates);
        foreach (var wi in config.WatchItems)
        {
            var c = FromWatchItem(wi, templates); c.Parent = root; root.Children.Add(c);
        }
        return root;
    }

    /// <summary>Templates by ID, for expanding Ref nodes inline.</summary>
    private static Dictionary<string, TemplateConfig> TemplateIndex(List<TemplateConfig>? templates)
    {
        var index = new Dictionary<string, TemplateConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in templates ?? [])
            if (!string.IsNullOrWhiteSpace(t.ID)) index[t.ID] = t;
        return index;
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

        var index = TemplateIndex(templates);
        foreach (var t in templates)
        {
            var c = FromTemplate(t, index); c.Parent = root; root.Children.Add(c);
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

    public static TreeNodeViewModel FromWatchItem(WatchItemConfig wi, IReadOnlyDictionary<string, TemplateConfig>? templates = null)
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
        foreach (var ev in wi.Events) { var c = FromEvent(ev, templates); c.Parent = node; node.Children.Add(c); }
        return node;
    }

    public static TreeNodeViewModel FromEvent(EventConfig ev, IReadOnlyDictionary<string, TemplateConfig>? templates = null)
    {
        var node = new TreeNodeViewModel
        {
            NodeKind = NodeKinds.Event, NodeIcon = "E",
            NodeIconGlyph = ResolveNodeIconGlyph(NodeKinds.Event),
            EventType = ev.Type,
            ExecutionTypeText = ev.ExecutionType.ToString(),
            DisplayText = $"Event: {ev.Type} ({ev.ExecutionType})", ModelObject = ev,
        };
        foreach (var child in ev.Children) { var c = FromActionNode(child, templates); c.Parent = node; node.Children.Add(c); }
        return node;
    }

    public static TreeNodeViewModel FromTemplate(TemplateConfig t, IReadOnlyDictionary<string, TemplateConfig>? templates = null)
    {
        var node = new TreeNodeViewModel
        {
            NodeKind = NodeKinds.Template, NodeIcon = "T",
            NodeIconGlyph = ResolveNodeIconGlyph(NodeKinds.Template),
            TemplateName = t.ID, Tag = t.ID,
            DisplayText = $"Template: {t.ID}", ModelObject = t,
        };
        foreach (var child in t.Children) { var c = FromActionNode(child, templates); c.Parent = node; node.Children.Add(c); }
        return node;
    }

    public static TreeNodeViewModel FromActionNode(IActionNode n, IReadOnlyDictionary<string, TemplateConfig>? templates = null) => n switch
    {
        ActionGroupConfig ag => FromActionGroup(ag, templates),
        ActionConfig a => FromAction(a),
        InitializeConfig init => FromInitialize(init),
        RefConfig r => FromRef(r, templates),
        _ => new TreeNodeViewModel { DisplayText = "Unknown" },
    };

    public static TreeNodeViewModel FromActionGroup(ActionGroupConfig ag, IReadOnlyDictionary<string, TemplateConfig>? templates = null)
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
        foreach (var child in ag.Children) { var c = FromActionNode(child, templates); c.Parent = node; node.Children.Add(c); }
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

    public static TreeNodeViewModel FromRef(RefConfig r, IReadOnlyDictionary<string, TemplateConfig>? templates = null)
        => FromRef(r, templates, null);

    /// <summary>
    /// A Ref shows the referenced template's nodes inline, resolved against the OWNING pipeline -
    /// the ancestor walk in <see cref="TokenScope"/> reaches the WatchItem, so the same template
    /// under SP2023R2SP2 and SP2026 previews different values.
    /// </summary>
    /// <remarks>
    /// The expansion is a read-only mirror of the template. Its nodes carry the TEMPLATE's model
    /// objects, so letting them write back would rewrite the shared template for every pipeline
    /// that Refs it - <see cref="ApplyToModel"/> refuses on <see cref="IsRefExpansion"/>.
    /// </remarks>
    private static TreeNodeViewModel FromRef(
        RefConfig r,
        IReadOnlyDictionary<string, TemplateConfig>? templates,
        HashSet<string>? visiting)
    {
        var node = new TreeNodeViewModel
        {
            NodeKind = NodeKinds.Ref, NodeIcon = ">",
            NodeIconGlyph = ResolveNodeIconGlyph(NodeKinds.Ref),
            TemplateID = r.TemplateID,
            DisplayText = $"Ref > {r.TemplateID}", ModelObject = r,
        };

        if (templates is null || string.IsNullOrWhiteSpace(r.TemplateID)) return node;
        if (!templates.TryGetValue(r.TemplateID, out var template)) return node;

        // A template that Refs itself, directly or through another, would recurse forever.
        visiting ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!visiting.Add(r.TemplateID)) return node;

        foreach (var child in template.Children)
        {
            var c = FromActionNodeExpanded(child, templates, visiting);
            c.Parent = node;
            node.Children.Add(c);
        }

        visiting.Remove(r.TemplateID);
        node.ChildCount = node.Children.Count;
        node.IsExpanded = false;
        return node;
    }

    /// <summary>Builds a Ref's descendant and marks the whole subtree as a read-only mirror.</summary>
    private static TreeNodeViewModel FromActionNodeExpanded(
        IActionNode n,
        IReadOnlyDictionary<string, TemplateConfig> templates,
        HashSet<string> visiting)
    {
        var node = n switch
        {
            ActionGroupConfig ag => FromActionGroupExpanded(ag, templates, visiting),
            RefConfig nested => FromRef(nested, templates, visiting),
            _ => FromActionNode(n, templates),
        };
        node.MarkRefExpansion();
        return node;
    }

    private static TreeNodeViewModel FromActionGroupExpanded(
        ActionGroupConfig ag,
        IReadOnlyDictionary<string, TemplateConfig> templates,
        HashSet<string> visiting)
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
        foreach (var child in ag.Children)
        {
            var c = FromActionNodeExpanded(child, templates, visiting);
            c.Parent = node;
            node.Children.Add(c);
        }
        return node;
    }

    private void MarkRefExpansion()
    {
        IsRefExpansion = true;
        foreach (var c in Children) c.MarkRefExpansion();
    }

    // ── Write-back & refresh ────────────────────────────────────────

    public void ApplyToModel()
    {
        // A Ref expansion mirrors the template's own model objects. Writing back here would edit
        // the template itself, silently changing every other pipeline that Refs it.
        if (IsRefExpansion) return;
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
