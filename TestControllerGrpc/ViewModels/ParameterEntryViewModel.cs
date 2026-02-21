using CommunityToolkit.Mvvm.ComponentModel;

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
}
