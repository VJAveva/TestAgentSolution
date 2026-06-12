using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Views.Dialogs;

/// <summary>
/// Force-release reason capture dialog — Mockup 9.
/// DataContext assigned via App.Services per popup-window convention.
/// </summary>
public partial class ForceReleaseReasonDialog : Window
{
    private readonly ForceReleaseReasonDialogViewModel _vm;

    public ForceReleaseReasonDialog()
    {
        _vm = App.Services.GetRequiredService<ForceReleaseReasonDialogViewModel>();
        DataContext = _vm;
        InitializeComponent();

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ForceReleaseReasonDialogViewModel.SubmitSucceeded) && _vm.SubmitSucceeded)
            {
                DialogResult = true;
            }
        };
    }

    public void Initialize(string pipelineId, string ownerDisplayName)
    {
        _vm.Initialize(pipelineId, ownerDisplayName);
    }
}
