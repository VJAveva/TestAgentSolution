using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using TestControllerGrpc.Features;

namespace TestControllerGrpc.ViewModels;

/// <summary>
/// Bindable on/off flags for the toggleable features. Views bind Visibility to these through BoolVis,
/// so a Features.json change hides or shows UI without a restart.
/// </summary>
public sealed partial class FeatureFlagsViewModel : ObservableObject
{
    private readonly IFeatureGate? _gate;

    [ObservableProperty] private bool _results = true;
    [ObservableProperty] private bool _firReport = true;
    [ObservableProperty] private bool _codeChurn = true;
    [ObservableProperty] private bool _impactMapping = true;
    [ObservableProperty] private bool _pipelineBuilder = true;
    [ObservableProperty] private bool _globalVariables = true;
    [ObservableProperty] private bool _reports = true;

    /// <summary>True when any feature in the Results/Report Card ribbon group is on, so the group never shows empty.</summary>
    [ObservableProperty] private bool _anyResultsGroup = true;

    public FeatureFlagsViewModel(IFeatureGate? gate)
    {
        _gate = gate;
        Refresh();
        if (_gate is not null)
            _gate.Changed += OnGateChanged;
    }

    public bool IsEnabled(string key) => _gate?.IsEnabled(key) ?? true;

    private void OnGateChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Refresh();
        else dispatcher.InvokeAsync(Refresh);
    }

    public void Refresh()
    {
        Results = IsEnabled(FeatureKeys.Results);
        FirReport = IsEnabled(FeatureKeys.FirReport);
        CodeChurn = IsEnabled(FeatureKeys.CodeChurn);
        ImpactMapping = IsEnabled(FeatureKeys.ImpactMapping);
        PipelineBuilder = IsEnabled(FeatureKeys.PipelineBuilder);
        GlobalVariables = IsEnabled(FeatureKeys.GlobalVariables);
        Reports = IsEnabled(FeatureKeys.Reports);
        AnyResultsGroup = Results || Reports;
        Changed?.Invoke();
    }

    /// <summary>Raised on the UI thread after the flags change, for code that builds menus imperatively.</summary>
    public event Action? Changed;
}
