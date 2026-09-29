using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Views.Dialogs;

/// <summary>
/// Checklist of registered agents for "Apply to agents...". The caller reads
/// <see cref="SelectedAgentNames"/> after a true dialog result.
/// </summary>
public partial class ApplyToAgentsDialog : Window
{
    private readonly ApplyToAgentsViewModel _vm;

    public ApplyToAgentsDialog(IEnumerable<AgentChoice> agents, string actionTag)
    {
        InitializeComponent();
        _vm = new ApplyToAgentsViewModel(agents, actionTag);
        DataContext = _vm;
    }

    /// <summary>Agents the user ticked, in registry order.</summary>
    public IReadOnlyList<string> SelectedAgentNames =>
        _vm.Agents.Where(a => a.IsSelected).Select(a => a.Name).ToList();

    private void OnApplyClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var a in _vm.Agents) a.IsSelected = true;
    }

    private void OnSelectNoneClick(object sender, RoutedEventArgs e)
    {
        foreach (var a in _vm.Agents) a.IsSelected = false;
    }
}

public sealed partial class ApplyToAgentsViewModel : ObservableObject
{
    public ApplyToAgentsViewModel(IEnumerable<AgentChoice> agents, string actionTag)
    {
        ActionTag = string.IsNullOrWhiteSpace(actionTag) ? "(untitled action)" : actionTag;
        foreach (var a in agents)
        {
            var row = new SelectableAgent(a.Name, a.Status);
            row.PropertyChanged += (_, _) => RefreshCount();
            Agents.Add(row);
        }
        RefreshCount();
    }

    public string ActionTag { get; }

    public ObservableCollection<SelectableAgent> Agents { get; } = new();

    [ObservableProperty] private string _countSummary = "";
    [ObservableProperty] private bool _hasSelection;

    private void RefreshCount()
    {
        var n = Agents.Count(a => a.IsSelected);
        HasSelection = n > 0;
        CountSummary = n == 0
            ? "No agents selected."
            : $"Will create {n} cop{(n == 1 ? "y" : "ies")} of '{ActionTag}' inside a new Parallel group.";
    }
}

public sealed partial class SelectableAgent : ObservableObject
{
    public SelectableAgent(string name, string status)
    {
        Name = name;
        Status = status;
    }

    public string Name { get; }
    public string Status { get; }
    public string Display => $"{Name}  ({Status})";

    [ObservableProperty] private bool _isSelected;
}
