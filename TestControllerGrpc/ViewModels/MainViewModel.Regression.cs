using System.Windows;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.ViewModels.Regression;
using TestControllerGrpc.Views.Regression;

namespace TestControllerGrpc.ViewModels;

// ── Regression tab: singleton window management ───────────────────
public sealed partial class MainViewModel
{
    private RegressionWindow? _regressionWindow;

    // A greyed-out button with no reason generates a support ticket; a hidden one generates two.
    private const string ToolsRestrictedToolTip = "Requires Administrator or Sr Manager";

    public bool CanOpenRegression => _capabilityChecker.Can(Permission.CodeChurn_View);

    public string OpenRegressionToolTip => CanOpenRegression
        ? "Open the CodeChurn tab (build/change impact planning)"
        : ToolsRestrictedToolTip;

    /// <summary>UX only — the WebApi permission gate is the enforcement.</summary>
    private void RefreshToolsPermissions()
    {
        OnPropertyChanged(nameof(CanOpenRegression));
        OnPropertyChanged(nameof(OpenRegressionToolTip));
        OnPropertyChanged(nameof(CanOpenReportCard));
        OnPropertyChanged(nameof(OpenReportCardToolTip));
        OpenRegressionCommand.NotifyCanExecuteChanged();
        OpenReportCardCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanOpenRegression))]
    private void OpenRegression()
    {
        if (_regressionWindow is not null && _regressionWindow.IsLoaded)
        {
            _regressionWindow.Activate();
            if (_regressionWindow.WindowState == WindowState.Minimized)
                _regressionWindow.WindowState = WindowState.Normal;
            return;
        }

        var vm = App.Services.GetService(typeof(RegressionViewModel)) as RegressionViewModel;
        if (vm is null) return;

        _regressionWindow = new RegressionWindow(vm);
        _regressionWindow.Closed += (_, _) => _regressionWindow = null;
        _regressionWindow.Show();
    }
}
