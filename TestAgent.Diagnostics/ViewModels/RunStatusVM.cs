using CommunityToolkit.Mvvm.ComponentModel;

namespace TestAgent.Diagnostics.ViewModels;

/// <summary>Live lifecycle state of a pipeline run, derived from the streaming records.</summary>
public enum RunState { Running, Completed, Failed }

/// <summary>
/// One row on the live status board: the current state of a single pipeline run,
/// keyed by RunId and updated in place as new records stream in from the tailer.
/// </summary>
public sealed partial class RunStatusVM : ObservableObject
{
    public string RunId { get; init; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(Glyph))]
    private RunState _state = RunState.Running;

    [ObservableProperty] private string _pipeline = "";
    [ObservableProperty] private string _agent = "";
    [ObservableProperty] private string _lastAction = "";
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private string _lastError = "";
    [ObservableProperty] private DateTime _startedUtc;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastUpdateText))]
    private DateTime _lastUpdate;

    public string RunIdShort => RunId.Length > 8 ? RunId[..8] : RunId;
    public string StartedText => StartedUtc.ToString("HH:mm:ss");
    public string LastUpdateText => LastUpdate.ToString("HH:mm:ss");

    public string StateText => State switch
    {
        RunState.Running => "Running",
        RunState.Failed => "Failed",
        _ => "Completed",
    };

    public string Glyph => State switch
    {
        RunState.Running => "\u25CF",    // filled circle (active)
        RunState.Failed => "\u2717",     // cross
        _ => "\u2713",                    // check
    };
}
