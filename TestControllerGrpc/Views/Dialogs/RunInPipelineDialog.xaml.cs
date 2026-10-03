using System.Windows;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Views.Dialogs;

/// <summary>Pipeline chooser for a Templates-library run.</summary>
public partial class RunInPipelineDialog : Window
{
    private readonly RunInPipelineDialogViewModel _vm;

    public RunInPipelineDialog(RunInPipelineDialogViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.CloseRequested += OnCloseRequested;
        Closed += (_, _) => vm.CloseRequested -= OnCloseRequested;
    }

    private void OnCloseRequested(bool confirmed)
    {
        DialogResult = confirmed;
        Close();
    }

    /// <summary>
    /// Shows the chooser and returns the chosen pipeline, or null when cancelled.
    /// </summary>
    public static string? Ask(Window? owner, string templateId, string nodeLabel, IEnumerable<string> pipelines)
    {
        var vm = new RunInPipelineDialogViewModel();
        vm.Initialize(templateId, nodeLabel, pipelines);

        var dlg = new RunInPipelineDialog(vm) { Owner = owner };
        dlg.ShowDialog();

        return vm.Confirmed ? vm.SelectedPipeline : null;
    }
}
