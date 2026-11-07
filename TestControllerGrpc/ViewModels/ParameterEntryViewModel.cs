using CommunityToolkit.Mvvm.ComponentModel;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels;

/// <summary>
/// Represents a single key-value parameter entry from an Initialize parameter file.
/// Displayed as an editable row in the Initialize node's property panel.
/// </summary>
public sealed partial class ParameterEntryViewModel : ObservableObject
{
    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _value = "";
    [ObservableProperty] private bool _isNew;

    /// <summary>Key and value as loaded from disk; null for a row added in the grid.</summary>
    public string? OriginalKey { get; set; }
    public string? OriginalValue { get; set; }

    /// <summary>Section the value was resolved from, so a save writes it back to the same place.</summary>
    public ParameterLayer Layer { get; set; } = ParameterLayer.Global;

    public string LayerLabel => Layer switch
    {
        ParameterLayer.Profile => "Profile",
        ParameterLayer.Pipeline => "Pipeline",
        ParameterLayer.GlobalVariables => "GlobalVariables.json",
        _ => "Global",
    };

    public bool IsChanged => OriginalKey is null
        || !string.Equals(Key, OriginalKey, StringComparison.Ordinal)
        || !string.Equals(Value, OriginalValue, StringComparison.Ordinal);
}

/// <summary>Turns Parameters-grid changes into edits for <see cref="ParameterFileWriter"/>.</summary>
public static class ParameterGridEdits
{
    public static IReadOnlyList<ParameterEdit> Compute(
        IEnumerable<ParameterEntryViewModel> rows,
        IEnumerable<ParameterEntryViewModel> removed,
        string? profile)
    {
        var edits = new List<ParameterEdit>();

        foreach (var row in removed.Where(r => r.OriginalKey is not null))
            edits.Add(new ParameterEdit(row.OriginalKey!, null, row.Layer));

        foreach (var row in rows.Where(r => r.IsChanged && !string.IsNullOrWhiteSpace(r.Key)))
        {
            if (row.OriginalKey is null)
            {
                // A new key goes to the narrowest section shown in the grid header: the selected profile.
                var layer = string.IsNullOrWhiteSpace(profile) ? ParameterLayer.Global : ParameterLayer.Profile;
                edits.Add(new ParameterEdit(row.Key.Trim(), row.Value, layer));
                continue;
            }

            if (!string.Equals(row.Key, row.OriginalKey, StringComparison.Ordinal))
                edits.Add(new ParameterEdit(row.OriginalKey, null, row.Layer));
            edits.Add(new ParameterEdit(row.Key.Trim(), row.Value, row.Layer));
        }

        return edits;
    }

    public static bool HasChanges(
        IEnumerable<ParameterEntryViewModel> rows, IEnumerable<ParameterEntryViewModel> removed) =>
        removed.Any(r => r.OriginalKey is not null) || rows.Any(r => r.IsChanged && !string.IsNullOrWhiteSpace(r.Key));
}
