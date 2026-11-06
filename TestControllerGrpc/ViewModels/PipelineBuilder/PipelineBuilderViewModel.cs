using System.Collections.ObjectModel;
using System.ComponentModel;
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
    [ObservableProperty] private BuilderResult? _preview;
    [ObservableProperty] private BuilderValidation? _validation;
    [ObservableProperty] private bool _isCreating;
    [ObservableProperty] private bool _isCreated;

    public ObservableCollection<TargetDescriptor> Targets { get; } = [];
    public ObservableCollection<Recipe> Recipes { get; } = [];
    public ObservableCollection<AgentPickItem> Agents { get; } = [];
    public ObservableCollection<AgentPickItem> VisibleAgents { get; } = [];
    public ObservableCollection<BuilderIssue> Blocking { get; } = [];
    public ObservableCollection<BuilderIssue> Informational { get; } = [];
    public ObservableCollection<BuilderIssue> Warnings { get; } = [];
    public ObservableCollection<BuilderIssue> RuntimeRequirements { get; } = [];
    public ObservableCollection<BuilderTokenReview> Tokens { get; } = [];
    public ObservableCollection<string> CatalogProblems { get; } = [];
    public string FragmentPath => Preview is null ? "" : Path.Combine(_fragmentDir, $"{Preview.Tag}.watchitem.xml");
    public string ProfileName => Preview?.WatchItem is { } item
        ? string.Join(", ", ParameterResolver.CollectInitializeNodes(item).Select(i => i.Profile).Distinct())
        : "";

    /// <summary>Authoring is Administrator-only; the panel is read-only for everyone else.</summary>
    public bool CanAuthor => _capabilities.Can(Permission.Pipeline_Author);

    public int SelectedAgentCount => Agents.Count(a => a.IsSelected);

    [RelayCommand(CanExecute = nameof(CanEdit))]
    public void Refresh()
    {
        var targetId = SelectedTarget?.Id;
        var recipeName = SelectedRecipe?.Name;
        var chosenAgents = Agents.Where(a => a.IsSelected).Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        InvalidatePreview();
        CatalogProblems.Clear();
        var targets = _builder.Targets();
        var recipes = _builder.Recipes();
        foreach (var problem in targets.Problems.Concat(recipes.Problems))
            CatalogProblems.Add(problem);
        Targets.Clear();
        foreach (var target in targets.Targets) Targets.Add(target);

        Recipes.Clear();
        foreach (var recipe in recipes.Recipes) Recipes.Add(recipe);
        SelectedTarget = Targets.FirstOrDefault(t => t.Id == targetId);
        SelectedRecipe = Recipes.FirstOrDefault(r => r.Name == recipeName);

        foreach (var agent in Agents) agent.PropertyChanged -= OnAgentPicked;
        Agents.Clear();
        foreach (var agent in _agentSource.GetAgents())
        {
            var item = new AgentPickItem(agent) { IsSelected = chosenAgents.Contains(agent.Name) };
            item.PropertyChanged += OnAgentPicked;
            Agents.Add(item);
        }

        ApplyAgentFilter();
        NotifyNavigation();
    }

    /// <summary>
    /// A tick on a row is the only thing that can satisfy the Agents step, and the count it feeds is
    /// computed - without this the Next button would stay disabled no matter what the user picked.
    /// </summary>
    private void OnAgentPicked(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AgentPickItem.IsSelected)) return;

        OnPropertyChanged(nameof(SelectedAgentCount));
        InvalidatePreview();
    }

    partial void OnCurrentStepChanged(BuilderStep value) => NotifyNavigation();

    partial void OnSelectedTargetChanged(TargetDescriptor? value) => InvalidatePreview();

    partial void OnSelectedRecipeChanged(Recipe? value) => InvalidatePreview();
    partial void OnIsCreatingChanged(bool value) => NotifyNavigation();
    partial void OnIsCreatedChanged(bool value) => NotifyNavigation();
    partial void OnPreviewChanged(BuilderResult? value)
    {
        OnPropertyChanged(nameof(FragmentPath));
        OnPropertyChanged(nameof(ProfileName));
        NotifyNavigation();
    }
    partial void OnValidationChanged(BuilderValidation? value) => NotifyNavigation();

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

    public bool CanEdit => !IsCreating;
    public bool CanGoBack => CanEdit && CurrentStep != BuilderStep.Target;
    public bool CanReview => CanEdit && !IsCreated && CurrentStep == BuilderStep.Review;
    public bool CanGoNext => CanEdit && (CurrentStep switch
    {
        BuilderStep.Target => SelectedTarget is not null,
        BuilderStep.Recipe => SelectedRecipe is not null,
        BuilderStep.Agents => SelectedAgentCount > 0,
        _ => false,
    });

    public string StepHint => IsCreating ? "Creating files. No pipeline will be activated or run."
        : IsCreated ? "Files created. Import the fragment in the WatchList editor, then run pre-flight before execution."
        : CurrentStep switch
        {
            BuilderStep.Target => "Step 1 of 4: choose a target / release to continue.",
            BuilderStep.Recipe => "Step 2 of 4: choose the stages to generate.",
            BuilderStep.Agents => "Step 3 of 4: select at least one agent. Availability is checked again at run time.",
            _ => "Step 4 of 4: check the files and token sources. Creating files does not activate or run the pipeline.",
        };

    private void NotifyNavigation()
    {
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(CanReview));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(StepHint));
        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        CreateCommand.NotifyCanExecuteChanged();
        BuildPreviewCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        ChangeStepCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void ChangeStep(BuilderStep step)
    {
        if (CanEdit && step != BuilderStep.Review) CurrentStep = step;
    }

    private void InvalidatePreview()
    {
        var hadPreview = Preview is not null;
        Preview = null;
        Validation = null;
        IsCreated = false;
        Blocking.Clear();
        Informational.Clear();
        Warnings.Clear();
        RuntimeRequirements.Clear();
        Tokens.Clear();
        if (hadPreview) Status = "Selections changed. Review again before creating files.";
        NotifyNavigation();
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
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

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        if (!CanGoBack) return;
        CurrentStep = CurrentStep switch
        {
            BuilderStep.Review => BuilderStep.Agents,
            BuilderStep.Agents => BuilderStep.Recipe,
            BuilderStep.Recipe => BuilderStep.Target,
            _ => CurrentStep,
        };
    }

    [RelayCommand(CanExecute = nameof(CanReview))]
    public void BuildPreview()
    {
        if (!CanReview) return;
        InvalidatePreview();

        if (SelectedTarget is null || SelectedRecipe is null || SelectedAgentCount == 0)
        {
            Status = "Choose a target, recipe and at least one agent before reviewing.";
            return;
        }

        var request = new BuilderRequest(
            SelectedTarget.Id,
            SelectedRecipe.Name,
            [.. Agents.Where(a => a.IsSelected).Select(a => a.Name)]);

        Preview = _builder.Preview(request);
        Validation = _builder.Validate(Preview, _liveConfig(), _fragmentDir);

        foreach (var issue in Validation.Blocking) Blocking.Add(issue);
        foreach (var issue in Validation.Informational) Informational.Add(issue);
        foreach (var issue in Validation.Informational.Where(i => i.Severity == BuilderSeverity.Warning)) Warnings.Add(issue);
        foreach (var issue in Validation.Informational.Where(i => i.Severity == BuilderSeverity.Info)) RuntimeRequirements.Add(issue);
        foreach (var token in Validation.Tokens) Tokens.Add(token);

        Status = Validation.CanCreate
            ? $"Ready to create files for '{Preview.Tag}'. Run-time readiness has not been checked."
            : $"{Validation.Blocking.Count} problem(s) must be fixed first.";

        OnPropertyChanged(nameof(CanCreate));
    }

    public bool CanCreate => CanEdit && !IsCreated && CurrentStep == BuilderStep.Review
        && CanAuthor && Preview is not null && Validation is { CanCreate: true };

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        if (!CanCreate || Preview is null || Validation is null)
        {
            Status = CanAuthor
                ? "Create is blocked until every problem is fixed."
                : "You do not have permission to author pipelines.";
            return;
        }

        IsCreating = true;
        try
        {
            var outcome = await _builder.CreateAsync(Preview, Validation, _fragmentDir, _userHolder.User, live: _liveConfig());
            IsCreated = outcome.Written;
            if (!outcome.Written)
            {
                Validation = null;
                Blocking.Clear();
                foreach (var issue in outcome.Issues.Where(i => i.Severity == BuilderSeverity.Error))
                    Blocking.Add(issue);
            }
            Status = string.Join(" ", outcome.Issues.Select(i => i.Message));
        }
        finally
        {
            IsCreating = false;
        }
    }
}
