using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Core.PipelineBuilder;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels.PipelineBuilder;

public enum BuilderStep { Target, Recipe, Agents, Review }

/// <summary>One row in the agent picker.</summary>
public sealed partial class AgentPickItem(BuilderAgent agent) : ObservableObject
{
    [ObservableProperty] private bool _isSelected;

    public string Name { get; } = agent.Name;
    public string Status { get; } = agent.Status;
    public bool IsOnline { get; } = agent.IsOnline;
    public bool IsBusy { get; } = agent.IsBusy;
}

/// <summary>
/// Drives the four-step New Pipeline panel over <see cref="PipelineBuilderService"/>.
/// </summary>
/// <remarks>
/// Create is gated on <see cref="Permission.Pipeline_Author"/>. That is a UX gate only - the same
/// permission has to be enforced server-side on the web path - but without it the panel would offer
/// a button that authors pipelines to anyone who can open the window.
/// </remarks>
public sealed partial class PipelineBuilderViewModel : ObservableObject
{
    private readonly PipelineBuilderService _builder;
    private readonly IBuilderAgentSource _agentSource;
    private readonly CapabilityChecker _capabilities;
    private readonly CurrentUserHolder _userHolder;
    private readonly Func<WatchListConfig> _liveConfig;
    private readonly string _fragmentDir;

    public PipelineBuilderViewModel(
        PipelineBuilderService builder,
        IBuilderAgentSource agentSource,
        CapabilityChecker capabilities,
        CurrentUserHolder userHolder,
        Func<WatchListConfig> liveConfig,
        string fragmentDir)
    {
        _builder = builder;
        _agentSource = agentSource;
        _capabilities = capabilities;
        _userHolder = userHolder;
        _liveConfig = liveConfig;
        _fragmentDir = fragmentDir;

        Refresh();
    }

    [ObservableProperty] private BuilderStep _currentStep = BuilderStep.Target;
    [ObservableProperty] private TargetDescriptor? _selectedTarget;
    [ObservableProperty] private Recipe? _selectedRecipe;
    [ObservableProperty] private string _agentFilter = "";
    [ObservableProperty] private bool _onlineOnly;
    [ObservableProperty] private string _status = "";

    public ObservableCollection<TargetDescriptor> Targets { get; } = [];
    public ObservableCollection<Recipe> Recipes { get; } = [];
    public ObservableCollection<AgentPickItem> Agents { get; } = [];
    public ObservableCollection<AgentPickItem> VisibleAgents { get; } = [];
    public ObservableCollection<BuilderIssue> Blocking { get; } = [];
    public ObservableCollection<BuilderIssue> Informational { get; } = [];

    public BuilderResult? Preview { get; private set; }
    public BuilderValidation? Validation { get; private set; }

    /// <summary>Authoring is Administrator-only; the panel is read-only for everyone else.</summary>
    public bool CanAuthor => _capabilities.Can(Permission.Pipeline_Author);

    public int SelectedAgentCount => Agents.Count(a => a.IsSelected);

    public void Refresh()
    {
        Targets.Clear();
        foreach (var target in _builder.Targets().Targets) Targets.Add(target);

        Recipes.Clear();
        foreach (var recipe in _builder.Recipes().Recipes) Recipes.Add(recipe);

        Agents.Clear();
        foreach (var agent in _agentSource.GetAgents()) Agents.Add(new AgentPickItem(agent));

        ApplyAgentFilter();
    }

    partial void OnAgentFilterChanged(string value) => ApplyAgentFilter();

    partial void OnOnlineOnlyChanged(bool value) => ApplyAgentFilter();

    /// <summary>
    /// Filters in place rather than rebuilding, so a selection survives typing in the search box -
    /// rebuilding the rows would silently drop agents the user had already ticked.
    /// </summary>
    private void ApplyAgentFilter()
    {
        VisibleAgents.Clear();
        foreach (var agent in Agents)
        {
            if (OnlineOnly && !agent.IsOnline) continue;

            if (!string.IsNullOrWhiteSpace(AgentFilter)
                && !agent.Name.Contains(AgentFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            VisibleAgents.Add(agent);
        }
    }

    [RelayCommand]
    private void SelectAllVisible()
    {
        foreach (var agent in VisibleAgents) agent.IsSelected = true;
        OnPropertyChanged(nameof(SelectedAgentCount));
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var agent in Agents) agent.IsSelected = false;
        OnPropertyChanged(nameof(SelectedAgentCount));
    }

    public bool CanGoNext => CurrentStep switch
    {
        BuilderStep.Target => SelectedTarget is not null,
        BuilderStep.Recipe => SelectedRecipe is not null,
        BuilderStep.Agents => SelectedAgentCount > 0,
        _ => false,
    };

    [RelayCommand]
    private void Next()
    {
        if (!CanGoNext) return;

        CurrentStep = CurrentStep switch
        {
            BuilderStep.Target => BuilderStep.Recipe,
            BuilderStep.Recipe => BuilderStep.Agents,
            BuilderStep.Agents => BuilderStep.Review,
            _ => CurrentStep,
        };

        if (CurrentStep == BuilderStep.Review) BuildPreview();
    }

    [RelayCommand]
    private void Back() => CurrentStep = CurrentStep switch
    {
        BuilderStep.Review => BuilderStep.Agents,
        BuilderStep.Agents => BuilderStep.Recipe,
        BuilderStep.Recipe => BuilderStep.Target,
        _ => CurrentStep,
    };

    public void BuildPreview()
    {
        Blocking.Clear();
        Informational.Clear();
        Preview = null;
        Validation = null;

        if (SelectedTarget is null || SelectedRecipe is null) return;

        var request = new BuilderRequest(
            SelectedTarget.Id,
            SelectedRecipe.Name,
            [.. Agents.Where(a => a.IsSelected).Select(a => a.Name)]);

        Preview = _builder.Preview(request);
        Validation = _builder.Validate(Preview, _liveConfig());

        foreach (var issue in Validation.Blocking) Blocking.Add(issue);
        foreach (var issue in Validation.Informational) Informational.Add(issue);

        Status = Validation.CanCreate
            ? $"Ready: '{Preview.Tag}' will be created."
            : $"{Validation.Blocking.Count} problem(s) must be fixed first.";

        OnPropertyChanged(nameof(CanCreate));
    }

    public bool CanCreate => CanAuthor && Validation is { CanCreate: true };

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (!CanCreate || Preview is null || Validation is null)
        {
            Status = CanAuthor
                ? "Create is blocked until every problem is fixed."
                : "You do not have permission to author pipelines.";
            return;
        }

        // The service authorizes again against the caller identity; this gate is only the affordance.
        var outcome = await _builder.CreateAsync(Preview, Validation, _fragmentDir, _userHolder.User);

        Status = outcome.Written
            ? $"Created. Import '{Path.GetFileName(outcome.FragmentPath)}' to activate '{Preview.Tag}'."
            : string.Join(" ", outcome.Issues.Select(i => i.Message));
    }
}
