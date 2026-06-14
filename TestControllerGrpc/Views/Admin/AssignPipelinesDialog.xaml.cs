using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.ViewModels.Admin;

namespace TestControllerGrpc.Views.Admin;

public partial class AssignPipelinesDialog : Window
{
    private readonly AssignPipelinesDialogViewModel _viewModel;

    public AssignPipelinesDialog()
    {
        InitializeComponent();
        _viewModel = App.Services.GetRequiredService<AssignPipelinesDialogViewModel>();
        DataContext = _viewModel;

        _viewModel.AssignmentsSaved += OnAssignmentsSaved;
    }

    public async Task InitializeAsync(string userId, string username, IReadOnlyList<string> allPipelineIds)
    {
        await _viewModel.InitializeAsync(userId, username, allPipelineIds);
    }

    private void OnAssignmentsSaved()
    {
        _viewModel.AssignmentsSaved -= OnAssignmentsSaved;
        Dispatcher.InvokeAsync(() =>
        {
            DialogResult = true;
        });
    }
}
