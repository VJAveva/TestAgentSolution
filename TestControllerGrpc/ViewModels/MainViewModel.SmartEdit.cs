using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Core.Maintenance;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using TestControllerGrpc.Views.Dialogs;

namespace TestControllerGrpc.ViewModels;

/// <summary>
/// Smart-editing support for the Node Properties panel: agent/template dropdowns, token
/// suggestions, debounced validation and the apply-to-agents fan-out.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly WatchFieldSuggestions _suggestions = new();
    private DispatcherTimer? _validationTimer;

    /// <summary>Registered agents with live status, for the AGENT NAME dropdown.</summary>
    public ObservableCollection<AgentChoice> AgentChoices { get; } = new();

    /// <summary>Template IDs, for the Ref node's TEMPLATE ID dropdown.</summary>
    public ObservableCollection<string> TemplateIds { get; } = new();

    /// <summary>Issues from the last debounced <see cref="WatchListValidator.Analyze"/> pass.</summary>
    public ObservableCollection<ValidationIssue> ValidationIssues { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveVocabularyCommand))]
    private bool _hasBlockingErrors;

    [ObservableProperty] private string _validationSummary = "";

    /// <summary>Save is blocked by Errors only; Warnings stay advisory.</summary>
    private bool CanSaveVocabulary() => !HasBlockingErrors;

    // ── wiring ──────────────────────────────────────────────────────

    private void InitializeSmartEdit()
    {
        _validationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _validationTimer.Tick += OnValidationTick;

        RefreshAgentChoices();
        RefreshTemplateIds();
        RunValidation();
    }

    private void DisposeSmartEdit()
    {
        if (_validationTrackedNode is not null)
        {
            _validationTrackedNode.PropertyChanged -= OnTrackedNodePropertyChanged;
            _validationTrackedNode = null;
        }
        if (_validationTimer is null) return;
        _validationTimer.Stop();
        _validationTimer.Tick -= OnValidationTick;
        _validationTimer = null;
    }

    /// <summary>Restarts the 300 ms debounce. Called from every edit path.</summary>
    public void QueueValidation()
    {
        if (_validationTimer is null) return;
        _validationTimer.Stop();
        _validationTimer.Start();
    }

    private TreeNodeViewModel? _validationTrackedNode;

    /// <summary>Follows the selected node's edits so typing re-validates without a save.</summary>
    private void TrackNodeEditsForValidation(TreeNodeViewModel? node)
    {
        if (ReferenceEquals(_validationTrackedNode, node)) return;

        if (_validationTrackedNode is not null)
            _validationTrackedNode.PropertyChanged -= OnTrackedNodePropertyChanged;

        _validationTrackedNode = node;

        if (_validationTrackedNode is not null)
            _validationTrackedNode.PropertyChanged += OnTrackedNodePropertyChanged;

        ApplyActionToAgentsCommand.NotifyCanExecuteChanged();
    }

    private void OnTrackedNodePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Selection and display churn are not edits; re-validating on them would run constantly.
        if (e.PropertyName is nameof(TreeNodeViewModel.IsSelected)
            or nameof(TreeNodeViewModel.IsExpanded)
            or nameof(TreeNodeViewModel.DisplayText)
            or nameof(TreeNodeViewModel.ResolvedDisplayText)
            or nameof(TreeNodeViewModel.ExecutionStatus)) return;

        QueueValidation();
    }

    private void OnValidationTick(object? sender, EventArgs e)
    {
        _validationTimer?.Stop();
        RunValidation();
    }

    private void RunValidation()
    {
        try
        {
            // Edits live on the tree view-models until written back, so the model must be
            // refreshed first or validation judges the previous keystroke's state.
            WriteBackAll();

            var roster = AgentChoices.Select(a => a.Name).ToList();
            var issues = WatchListValidator.Analyze(_config, roster);

            ValidationIssues.Clear();
            foreach (var issue in issues) ValidationIssues.Add(issue);

            var errors = issues.Count(i => i.Severity == WatchIssueSeverity.Error);
            var warnings = issues.Count(i => i.Severity == WatchIssueSeverity.Warning);
            HasBlockingErrors = errors > 0;
            ValidationSummary = errors == 0 && warnings == 0
                ? "No issues"
                : $"{errors} error(s), {warnings} warning(s)";
        }
        catch (Exception ex)
        {
            // Validation must never take the editor down; surface it and leave save unblocked.
            _logger.LogError(ex, "WatchList validation failed");
            HasBlockingErrors = false;
            ValidationSummary = $"Validation failed: {ex.Message}";
        }
    }

    // ── dropdown sources ────────────────────────────────────────────

    /// <summary>Rebuilds the agent dropdown from the live registry.</summary>
    public void RefreshAgentChoices()
    {
        var health = _dispatcher.GetAllAgentHealth();
        var rebootNeeded = _updateStatus?.GetAll()
            .Where(r => r.State == WindowsUpdateState.RebootRequired)
            .Select(r => r.NodeId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var previous = AgentChoices.Select(a => a.Name).ToList();
        var current = _dispatcher.RegisteredAgents
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        AgentChoices.Clear();
        foreach (var name in current)
        {
            AgentChoices.Add(new AgentChoice(
                name,
                DescribeAgent(name, health, rebootNeeded)));
        }

        _suggestions.AgentNames = current;

        // The roster feeds the unknown-agent rule, so a roster change can change the verdict.
        if (!previous.SequenceEqual(current, StringComparer.OrdinalIgnoreCase))
            QueueValidation();
    }

    private string DescribeAgent(
        string name,
        IReadOnlyDictionary<string, AgentHealthState> health,
        HashSet<string>? rebootNeeded)
    {
        if (_dispatcher.IsAgentExecuting(name)) return "busy";
        if (rebootNeeded?.Contains(name) == true) return "reboot";
        if (health.TryGetValue(name, out var h) && h.CircuitOpenedUtc.HasValue) return "offline";
        return "free";
    }

    /// <summary>Rebuilds the Ref template-id dropdown from the loaded config.</summary>
    public void RefreshTemplateIds()
    {
        TemplateIds.Clear();
        foreach (var id in _config.Templates
            .Select(t => t.ID)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            TemplateIds.Add(id);
        }
    }

    /// <summary>
    /// Token suggestions for the Command / Parameters editors: every known token with its
    /// currently resolved value. Secret values are masked - this list is rendered on screen.
    /// </summary>
    public IReadOnlyList<TokenSuggestion> GetTokenSuggestions()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<TokenSuggestion>();

        foreach (var (key, value) in TreeNodeViewModel.TokenValues)
        {
            if (!seen.Add(key)) continue;
            result.Add(new TokenSuggestion($"[_{key.TrimStart('_')}]", Mask(key, value)));
        }

        foreach (var token in _suggestions.AvailableTokens)
        {
            var key = token.Trim('[', ']');
            if (!seen.Add(key)) continue;
            result.Add(new TokenSuggestion(token, "(unresolved)"));
        }

        return result.OrderBy(t => t.Token, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static readonly string[] SecretKeyFragments =
        ["password", "passwd", "pwd", "secret", "token", "apikey", "api_key", "credential"];

    private static string Mask(string key, string value) =>
        SecretKeyFragments.Any(f => key.Contains(f, StringComparison.OrdinalIgnoreCase))
            ? "********"
            : value;

    // ── apply to agents ─────────────────────────────────────────────

    private bool CanApplyActionToAgents() =>
        SelectedNode?.NodeKind == NodeKinds.Action && AgentChoices.Count > 0;

    /// <summary>
    /// Replaces the selected action with a Parallel ActionGroup holding one copy per chosen agent.
    /// </summary>
    /// <remarks>
    /// The agent set is chosen explicitly in a dialog rather than inferred from who is free: the
    /// WatchList is persisted config, so a set derived from transient status would make the same
    /// click produce a different pipeline each time. There is no undo stack in this app, so the
    /// dialog's explicit count is the only safety net.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanApplyActionToAgents))]
    private void ApplyActionToAgents()
    {
        if (SelectedNode is not { NodeKind: NodeKinds.Action } node) return;
        if (node.ModelObject is not ActionConfig action) return;
        if (node.Parent is not { } parentNode) return;

        WriteBackAll();

        var dialog = new ApplyToAgentsDialog(AgentChoices, action.Tag)
        {
            Owner = FindOwnerWindow(),
        };
        if (dialog.ShowDialog() != true) return;

        var chosen = dialog.SelectedAgentNames;
        if (chosen.Count == 0) return;

        var group = BuildAgentFanOutGroup(action, chosen);

        if (!ReplaceActionWithGroup(parentNode, node, action, group))
        {
            AddLog("Apply to agents: could not locate the action's parent.", LogSeverity.Error);
            return;
        }

        IsDirty = true;
        AddLog($"Apply to agents: created {chosen.Count} copies of '{action.Tag}' in a Parallel group.",
            LogSeverity.Success);
        QueueValidation();
    }

    /// <summary>One clone of <paramref name="action"/> per agent, inside a new Parallel group.</summary>
    internal static ActionGroupConfig BuildAgentFanOutGroup(
        ActionConfig action, IReadOnlyList<string> agentNames)
    {
        var group = new ActionGroupConfig
        {
            Tag = string.IsNullOrWhiteSpace(action.Tag) ? "Fan-out" : $"{action.Tag} (all agents)",
            ExecutionType = ExecutionMode.Parallel,
        };

        foreach (var agent in agentNames)
        {
            var copy = action.Clone();
            copy.Type = ActionType.RunRemoteCommand;
            copy.AgentName = agent;
            copy.Tag = string.IsNullOrWhiteSpace(action.Tag) ? agent : $"{action.Tag} - {agent}";
            group.Children.Add(copy);
        }

        return group;
    }

    private bool ReplaceActionWithGroup(
        TreeNodeViewModel parentNode, TreeNodeViewModel actionNode,
        ActionConfig action, ActionGroupConfig group)
    {
        // Mirror the model mutation the delete/move paths use - the tree and the config are two
        // parallel structures and must be kept in step.
        var children = parentNode.ModelObject switch
        {
            EventConfig ev => ev.Children,
            ActionGroupConfig ag => ag.Children,
            TemplateConfig tc => tc.Children,
            _ => null,
        };
        if (children is null) return false;

        var modelIndex = children.IndexOf(action);
        if (modelIndex < 0) return false;
        children[modelIndex] = group;

        var treeIndex = parentNode.Children.IndexOf(actionNode);
        if (treeIndex < 0) return false;

        var groupNode = TreeNodeViewModel.FromActionNode(group);
        groupNode.Parent = parentNode;
        parentNode.Children[treeIndex] = groupNode;
        groupNode.IsExpanded = true;

        parentNode.RefreshDisplayText();
        SelectedNode = groupNode;
        return true;
    }
}

/// <summary>An agent offered in the AGENT NAME dropdown, with its live status.</summary>
public sealed record AgentChoice(string Name, string Status)
{
    public string Display => $"{Name}  ({Status})";
}

/// <summary>A token offered in the Command / Parameters editors, with its resolved value.</summary>
public sealed record TokenSuggestion(string Token, string ResolvedValue)
{
    public string Display => $"{Token}  =  {ResolvedValue}";
}
