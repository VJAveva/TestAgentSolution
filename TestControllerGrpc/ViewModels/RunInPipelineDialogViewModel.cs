using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TestControllerGrpc.ViewModels;

/// <summary>
/// Asks which pipeline a Templates-library run should borrow its settings from.
/// </summary>
/// <remarks>
/// A Template carries no Initialize, so running one straight from the library leaves every [Token]
/// unresolved and the run fails on the first action. The candidates are the pipelines that Ref the
/// template; picking one supplies its parameters and makes the run behave exactly as it would in
/// place. The dialog is never shown with an empty list - the caller disables the command instead.
/// </remarks>
public sealed partial class RunInPipelineDialogViewModel : ObservableObject
{
    [ObservableProperty] private string _templateId = "";
    [ObservableProperty] private string _nodeLabel = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private string? _selectedPipeline;

    public ObservableCollection<string> Pipelines { get; } = new();

    /// <summary>True when the user picked a pipeline rather than cancelling.</summary>
    public bool Confirmed { get; private set; }

    /// <summary>Raised when the dialog should close; the argument is <see cref="Confirmed"/>.</summary>
    public event Action<bool>? CloseRequested;

    public void Initialize(string templateId, string nodeLabel, IEnumerable<string> pipelines)
    {
        TemplateId = templateId;
        NodeLabel = nodeLabel;

        Pipelines.Clear();
        foreach (var p in pipelines) Pipelines.Add(p);

        // Pre-select when there is no real choice, so the common case is one keypress.
        SelectedPipeline = Pipelines.Count > 0 ? Pipelines[0] : null;
    }

    private bool CanRun => !string.IsNullOrWhiteSpace(SelectedPipeline);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Run()
    {
        Confirmed = true;
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        Confirmed = false;
        CloseRequested?.Invoke(false);
    }
}
