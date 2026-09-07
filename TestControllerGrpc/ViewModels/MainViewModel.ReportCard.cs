using System.Windows;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.ViewModels.Results;
using TestControllerGrpc.Views.Results;

namespace TestControllerGrpc.ViewModels;

// ── Build Report Card: singleton window management ───────────────────
public sealed partial class MainViewModel
{
    private BuildReportCardWindow? _reportCardWindow;

    public bool CanOpenReportCard => _capabilityChecker.Can(Permission.ReportCard_View);

    public string OpenReportCardToolTip => CanOpenReportCard
        ? "Open the Build Report Card"
        : ToolsRestrictedToolTip;

    [RelayCommand(CanExecute = nameof(CanOpenReportCard))]
    private void OpenReportCard()
    {
        if (_reportCardWindow is not null && _reportCardWindow.IsLoaded)
        {
            _reportCardWindow.Activate();
            if (_reportCardWindow.WindowState == WindowState.Minimized)
                _reportCardWindow.WindowState = WindowState.Normal;
            return;
        }

        var vm = App.Services.GetService(typeof(BuildReportCardViewModel)) as BuildReportCardViewModel;
        if (vm is null) return;

        _reportCardWindow = new BuildReportCardWindow(vm);
        _reportCardWindow.Closed += (_, _) => _reportCardWindow = null;
        _reportCardWindow.Show();
    }
}
