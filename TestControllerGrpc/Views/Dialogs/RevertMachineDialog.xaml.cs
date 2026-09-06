using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.ViewModels.AgentWorkspace;

namespace TestControllerGrpc.Views.Dialogs;

/// <summary>Single-node revert dialog. VM resolved from App.Services per the popup-window convention.</summary>
public partial class RevertMachineDialog : Window
{
    private readonly RevertMachineViewModel _vm;

    public RevertMachineDialog()
    {
        _vm = App.Services.GetRequiredService<RevertMachineViewModel>();
        DataContext = _vm;
        InitializeComponent();

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RevertMachineViewModel.SubmitSucceeded) && _vm.SubmitSucceeded)
                DialogResult = true;
        };
    }

    public void Initialize(string agentName) => _vm.Initialize(agentName);
}
