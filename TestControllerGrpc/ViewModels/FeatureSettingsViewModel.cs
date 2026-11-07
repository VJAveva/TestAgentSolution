using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Features;
using TestControllerGrpc.Identity;

namespace TestControllerGrpc.ViewModels;

public sealed partial class FeatureRowViewModel : ObservableObject
{
    public string Key { get; init; } = "";
    public string Description { get; init; } = "";
    public string Note { get; init; } = "";
    [ObservableProperty] private bool _isOn;
}

/// <summary>Admin > Features: one switch per toggleable feature, saved to Features.json.</summary>
public sealed partial class FeatureSettingsViewModel : ObservableObject
{
    private readonly IFeatureGate _gate;
    private readonly IFeatureSettingsService _settings;
    private readonly IUserContext? _user;

    public ObservableCollection<FeatureRowViewModel> Rows { get; } = new();
    public string ConfigPath => _gate.ConfigPath;
    [ObservableProperty] private string _teamName = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusIsError;

    public FeatureSettingsViewModel(IFeatureGate gate, IFeatureSettingsService settings, IUserContext? user)
    {
        _gate = gate;
        _settings = settings;
        _user = user;
        Load();
        if (_gate.LastError is { } error)
        {
            Status = error;
            StatusIsError = true;
        }
    }

    public void Load()
    {
        TeamName = _gate.TeamName;
        Rows.Clear();
        foreach (var f in _gate.All())
        {
            var note = f.OffBecause ?? (f.DependsOn is { } parent ? $"Depends on {parent}." : "");
            Rows.Add(new FeatureRowViewModel { Key = f.Key, Description = f.Description, Note = note, IsOn = f.ConfiguredOn });
        }
    }

    [RelayCommand]
    private async Task Save()
    {
        var desired = Rows.ToDictionary(r => r.Key, r => r.IsOn, StringComparer.OrdinalIgnoreCase);
        var result = await _settings.SaveAsync(desired, _user, null, CancellationToken.None);
        StatusIsError = !result.Success;
        Status = result.Success ? result.Message : $"Save failed: {result.Message}";
        if (result.Success) Load();
    }
}
