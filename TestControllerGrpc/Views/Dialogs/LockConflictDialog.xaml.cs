using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Views.Dialogs;

/// <summary>
/// Lock conflict dialog — Mockup 6.
/// DataContext assigned via App.Services per popup-window convention.
/// </summary>
public partial class LockConflictDialog : Window
{
    private readonly LockConflictDialogViewModel _vm;

    public LockConflictDialog()
    {
        _vm = App.Services.GetRequiredService<LockConflictDialogViewModel>();
        DataContext = _vm;
        InitializeComponent();

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LockConflictDialogViewModel.RequestClose))
            {
                if (_vm.RequestClose) DialogResult = false;
            }
        };

        _vm.ForceReleaseRequested += OnForceReleaseRequested;
    }

    public void Initialize(TestController.Api.Contracts.PipelineLockDto dto)
    {
        _vm.Initialize(dto);
    }

    /// <summary>True if the user chose to force-release from this dialog.</summary>
    public bool ForceReleaseChosen { get; private set; }

    private void OnForceReleaseRequested()
    {
        ForceReleaseChosen = true;
        DialogResult = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.ForceReleaseRequested -= OnForceReleaseRequested;
        _vm.Dispose();
        base.OnClosed(e);
    }
}
