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

/// <summary>One editable Code Churn repository; lists are comma-separated in the grid.</summary>
public sealed partial class CodeChurnRepositoryRowViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _url = "";
    [ObservableProperty] private string _branches = "";
    [ObservableProperty] private string _includePaths = "";
    [ObservableProperty] private string _excludePaths = "";
    [ObservableProperty] private string _credentialRef = "";

    public static CodeChurnRepositoryRowViewModel From(CodeChurnRepository r) => new()
    {
        Name = r.Name,
        Url = r.Url ?? "",
        Branches = string.Join(", ", r.Branches),
        IncludePaths = string.Join(", ", r.IncludePaths),
        ExcludePaths = string.Join(", ", r.ExcludePaths),
        CredentialRef = r.CredentialRef ?? "",
    };

    public CodeChurnRepository ToModel() => new(
        Name.Trim(),
        string.IsNullOrWhiteSpace(Url) ? null : Url.Trim(),
        Split(Branches), Split(IncludePaths), Split(ExcludePaths),
        string.IsNullOrWhiteSpace(CredentialRef) ? null : CredentialRef.Trim());

    private static List<string> Split(string value) =>
        value.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

/// <summary>Admin > Features: one switch per toggleable feature, plus the Code Churn repositories, saved to Features.json.</summary>
public sealed partial class FeatureSettingsViewModel : ObservableObject
{
    private readonly IFeatureGate _gate;
    private readonly IFeatureSettingsService _settings;
    private readonly IUserContext? _user;

    public ObservableCollection<FeatureRowViewModel> Rows { get; } = new();
    public ObservableCollection<CodeChurnRepositoryRowViewModel> Repositories { get; } = new();
    public string ConfigPath => _gate.ConfigPath;
    [ObservableProperty] private string _teamName = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private CodeChurnRepositoryRowViewModel? _selectedRepository;

    /// <summary>Text under the repository grid: what an empty list means for this install.</summary>
    [ObservableProperty] private string _repositoriesNote = "";

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

        Repositories.Clear();
        foreach (var r in _gate.CodeChurnRepositories ?? [])
            Repositories.Add(CodeChurnRepositoryRowViewModel.From(r));
        RepositoriesNote = _gate.CodeChurnRepositories is null
            ? "Not configured: Code Churn scans every component in the component map. Add a repository to limit it."
            : _gate.CodeChurnRepositories.Count == 0
                ? "Empty list: Code Churn shows \"No repositories configured\"."
                : "";
    }

    [RelayCommand]
    private void AddRepository()
    {
        var row = new CodeChurnRepositoryRowViewModel();
        Repositories.Add(row);
        SelectedRepository = row;
    }

    [RelayCommand]
    private void RemoveRepository()
    {
        if (SelectedRepository is { } row) Repositories.Remove(row);
    }

    [RelayCommand]
    private async Task Save()
    {
        var desired = Rows.ToDictionary(r => r.Key, r => r.IsOn, StringComparer.OrdinalIgnoreCase);
        var result = await _settings.SaveAsync(desired, _user, null, CancellationToken.None);
        if (!result.Success)
        {
            StatusIsError = true;
            Status = $"Save failed: {result.Message}";
            return;
        }

        var messages = new List<string>();
        if (result.Changes.Count > 0) messages.Add(result.Message);
        var repos = Repositories.Select(r => r.ToModel()).ToList();
        var unchanged = _gate.CodeChurnRepositories is null
            ? repos.Count == 0
            : CodeChurnRepositoryRules.Fingerprint(repos) == CodeChurnRepositoryRules.Fingerprint(_gate.CodeChurnRepositories);
        if (!unchanged)
        {
            var repoResult = await _settings.SaveCodeChurnAsync(repos, _user, null, CancellationToken.None);
            if (!repoResult.Success)
            {
                StatusIsError = true;
                Status = $"Features saved, but Code Churn repositories were not: {repoResult.Message}";
                return;
            }
            messages.Add(repoResult.Message);
        }

        StatusIsError = false;
        Status = messages.Count == 0 ? "No changes to save." : string.Join(" ", messages);
        Load();
    }
}
