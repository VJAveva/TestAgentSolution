using CommunityToolkit.Mvvm.ComponentModel;

namespace TestControllerGrpc.ViewModels.Execution;

/// <summary>
/// ViewModel for a single action pill in the agent row chain.
/// Status flow: Pending ? Running ? Success/Failed/Skipped.
/// </summary>
public partial class ActionPillVM : ObservableObject
{
    [ObservableProperty] private string _tag = "";
    [ObservableProperty] private string _actionType = "";
    [ObservableProperty] private string _command = "";
    [ObservableProperty] private string _agentName = "";
    [ObservableProperty] private string _status = "Pending";
    [ObservableProperty] private int _exitCode;
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private string _duration = "";
    [ObservableProperty] private int _progressPercent;
    [ObservableProperty] private DateTime _startedUtc = DateTime.UtcNow;
    [ObservableProperty] private double _durationSeconds;

    /// <summary>Stable identity used to update an existing pill in place.</summary>
    public string Key => string.IsNullOrEmpty(Tag)
        ? $"{ActionType}|{Command}"
        : Tag;

    /// <summary>Short display label (max ~22 chars).</summary>
    public string DisplayLabel
    {
        get
        {
            var label = !string.IsNullOrEmpty(Tag) ? Tag : Command;
            if (string.IsNullOrEmpty(label)) return "Action";

            if (label.Contains('\\'))
                label = System.IO.Path.GetFileNameWithoutExtension(label);

            if (label.Length > 22)
                label = label[..20] + "..";

            if (Status == "Running" && ProgressPercent > 0)
                label += $" {ProgressPercent}%";

            return label;
        }
    }

    public string StatusIcon => Status switch
    {
        "Success" => "\u2713",
        "Failed"  => "\u2717",
        "Running" => "\u25CF",
        "Skipped" => "\u2212",
        _         => "\u25CB",
    };

    public string Tooltip
    {
        get
        {
            var lines = new List<string>();
            if (!string.IsNullOrEmpty(Command))
                lines.Add($"Command: {Command}");
            lines.Add($"Status: {Status}");
            if (ExitCode != 0)
                lines.Add($"Exit code: {ExitCode}");
            if (!string.IsNullOrEmpty(ErrorMessage))
                lines.Add($"Error: {ErrorMessage}");
            if (!string.IsNullOrEmpty(Duration))
                lines.Add($"Duration: {Duration}");
            return string.Join("\n", lines);
        }
    }

    // ── Dirty-tracking to suppress redundant cascading notifications ──
    // The 1-second ReconcileCard tick re-applies all pill values even when
    // unchanged.  Every OnPropertyChanged cascades through WPF's binding
    // system and UIAutomationCore's peer tree.  With 50+ pills per session
    // that yields thousands of synchronous stack frames per tick – enough
    // to overflow the 1 MB default stack.  Guard each cascade with a
    // cached-value check so it only fires on actual changes.
    private string _cachedDisplayLabel = "";
    private string _cachedStatusIcon = "";
    private string _cachedTooltip = "";
    private string _cachedKey = "";

    private void RaiseDerivedIfChanged()
    {
        var dl = DisplayLabel;
        if (dl != _cachedDisplayLabel) { _cachedDisplayLabel = dl; OnPropertyChanged(nameof(DisplayLabel)); }

        var si = StatusIcon;
        if (si != _cachedStatusIcon)   { _cachedStatusIcon = si;  OnPropertyChanged(nameof(StatusIcon)); }

        var tt = Tooltip;
        if (tt != _cachedTooltip)      { _cachedTooltip = tt;     OnPropertyChanged(nameof(Tooltip)); }
    }

    partial void OnStatusChanged(string value) => RaiseDerivedIfChanged();

    partial void OnProgressPercentChanged(int value)
    {
        var dl = DisplayLabel;
        if (dl != _cachedDisplayLabel) { _cachedDisplayLabel = dl; OnPropertyChanged(nameof(DisplayLabel)); }
    }

    partial void OnTagChanged(string value)
    {
        var k = Key;
        if (k != _cachedKey) { _cachedKey = k; OnPropertyChanged(nameof(Key)); }
    }

    partial void OnCommandChanged(string value)
    {
        var k = Key;
        if (k != _cachedKey) { _cachedKey = k; OnPropertyChanged(nameof(Key)); }
    }
}
